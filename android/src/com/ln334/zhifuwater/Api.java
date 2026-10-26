package com.ln334.zhifuwater;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.ConnectException;
import java.net.HttpURLConnection;
import java.net.SocketTimeoutException;
import java.net.URL;
import java.net.UnknownHostException;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * 极简 HTTP 客户端：HttpURLConnection + 线程池 + 主线程回调，JSON 使用 org.json。
 * 所有接口统一 {@code Authorization: Bearer <token>}。
 */
public final class Api {

    /** 统一的异步回调：成功时 root 为完整响应体（含 data），失败时 error 为中文提示。 */
    public interface Cb {
        void done(JSONObject root, String error);
    }

    private static final ExecutorService POOL = Executors.newFixedThreadPool(3);
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    /** 令牌失效（HTTP 401）时的全局处理，由 MainActivity 设置。 */
    public static volatile Runnable onUnauthorized;

    private Api() {
    }

    public static void login(Context c, String baseUrl, String username, String password, Cb cb) {
        JSONObject body = new JSONObject();
        try {
            body.put("username", username);
            body.put("password", password);
        } catch (JSONException ignored) {
            // 字段均为字符串，不会失败
        }
        run("POST", join(baseUrl, "/api/auth/login"), null, body.toString(), cb);
    }

    public static void get(Context c, String path, Cb cb) {
        Session s = Session.of(c);
        if (s.isLocal()) {
            local(c, "GET", path, null, cb);
            return;
        }
        if (s.isDemo()) {
            demo(c, "GET", path, null, cb);
            return;
        }
        run("GET", join(s.baseUrl(), path), s.token(), null, cb);
    }

    public static void post(Context c, String path, JSONObject body, Cb cb) {
        Session s = Session.of(c);
        if (s.isLocal()) {
            local(c, "POST", path, body, cb);
            return;
        }
        if (s.isDemo()) {
            demo(c, "POST", path, body, cb);
            return;
        }
        run("POST", join(s.baseUrl(), path), s.token(), body == null ? "{}" : body.toString(), cb);
    }

    /**
     * 本机模式：不走网络，直接读 LocalDb + LocalEngine 的内存/SQLite 数据，
     * 返回结构与服务器完全一致，页面无需区分数据来源。
     */
    private static void local(final Context c, final String method, final String path,
                              final JSONObject body, final Cb cb) {
        LocalEngine.get(c).start();
        POOL.execute(new Runnable() {
            @Override
            public void run() {
                final JSONObject root = LocalApi.respond(c, method, path, body);
                final boolean ok = root != null && root.optBoolean("ok", false);
                final String message;
                if (ok) {
                    message = null;
                } else {
                    JSONObject err = root == null ? null : root.optJSONObject("error");
                    String m = err == null ? null : err.optString("message", "");
                    message = m == null || m.length() == 0 ? "本机数据处理失败" : m;
                }
                MAIN.post(new Runnable() {
                    @Override
                    public void run() {
                        cb.done(ok ? root : null, message);
                    }
                });
            }
        });
    }

    /** 离线演示模式：读取打包进 APK 的接口快照，在内存中处理确认/流转/提交。 */
    private static void demo(final Context c, final String method, final String path,
                             final JSONObject body, final Cb cb) {
        POOL.execute(new Runnable() {
            @Override
            public void run() {
                final JSONObject root = Demo.respond(c, method, path, body);
                final boolean ok = root != null && root.optBoolean("ok", false);
                final String message;
                if (ok) {
                    message = null;
                } else {
                    JSONObject err = root == null ? null : root.optJSONObject("error");
                    String m = err == null ? null : err.optString("message", "");
                    message = m == null || m.length() == 0 ? "离线演示暂不支持该操作" : m;
                }
                MAIN.post(new Runnable() {
                    @Override
                    public void run() {
                        cb.done(ok ? root : null, message);
                    }
                });
            }
        });
    }

    private static String join(String base, String path) {
        String b = base == null ? "" : base.trim();
        while (b.endsWith("/")) b = b.substring(0, b.length() - 1);
        if (path == null) return b;
        if (path.startsWith("/")) return b + path;
        return b + "/" + path;
    }

    private static void run(final String method, final String url, final String token,
                            final String body, final Cb cb) {
        POOL.execute(new Runnable() {
            @Override
            public void run() {
                JSONObject root = null;
                String error = null;
                boolean unauthorized = false;
                try {
                    root = request(method, url, token, body);
                } catch (ApiError e) {
                    error = e.getMessage();
                    unauthorized = e.http == 401;
                } catch (SocketTimeoutException e) {
                    error = "连接服务器超时，请稍后重试（超时 10 秒）";
                } catch (UnknownHostException e) {
                    error = "无法解析服务器地址，请检查地址是否正确";
                } catch (ConnectException e) {
                    error = "无法连接服务器，请确认平台服务已启动、手机与电脑在同一网络";
                } catch (Exception e) {
                    String m = e.getMessage();
                    error = "网络请求失败" + (m == null || m.length() == 0 ? "" : "：" + m);
                }
                final JSONObject finalRoot = root;
                final String finalError = error;
                final boolean finalUnauthorized = unauthorized;
                MAIN.post(new Runnable() {
                    @Override
                    public void run() {
                        if (finalUnauthorized) {
                            Runnable h = onUnauthorized;
                            if (h != null) h.run();
                        }
                        cb.done(finalRoot, finalError);
                    }
                });
            }
        });
    }

    private static JSONObject request(String method, String urlStr, String token, String body)
            throws Exception {
        HttpURLConnection conn = null;
        try {
            URL url = new URL(urlStr);
            conn = (HttpURLConnection) url.openConnection();
            conn.setConnectTimeout(10000);
            conn.setReadTimeout(10000);
            conn.setRequestMethod(method);
            conn.setUseCaches(false);
            conn.setInstanceFollowRedirects(true);
            conn.setRequestProperty("Accept", "application/json");
            conn.setRequestProperty("Accept-Encoding", "identity");
            if (token != null && token.length() > 0) {
                conn.setRequestProperty("Authorization", "Bearer " + token);
            }
            if (body != null) {
                conn.setDoOutput(true);
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                byte[] bytes = body.getBytes("UTF-8");
                conn.setFixedLengthStreamingMode(bytes.length);
                OutputStream os = conn.getOutputStream();
                try {
                    os.write(bytes);
                    os.flush();
                } finally {
                    os.close();
                }
            }
            int code = conn.getResponseCode();
            InputStream in = code >= 400 ? conn.getErrorStream() : conn.getInputStream();
            String text = readAll(in);
            JSONObject root;
            try {
                root = new JSONObject(text.length() == 0 ? "{}" : text);
            } catch (JSONException e) {
                throw new ApiError(code, "服务器返回了无法解析的数据（HTTP " + code + "）");
            }
            if (!root.optBoolean("ok", false)) {
                String msg = errorMessage(root);
                throw new ApiError(code, msg == null ? ("请求失败（HTTP " + code + "）") : msg);
            }
            return root;
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private static String errorMessage(JSONObject root) {
        JSONObject err = root.optJSONObject("error");
        if (err != null) {
            String m = err.optString("message", "");
            if (m.length() > 0) return m;
        }
        String m = root.optString("message", "");
        if (m.length() > 0) return m;
        if (root.has("error") && root.opt("error") instanceof String) {
            String s = root.optString("error", "");
            if (s.length() > 0) return s;
        }
        return null;
    }

    private static String readAll(InputStream in) throws Exception {
        if (in == null) return "";
        BufferedReader reader = new BufferedReader(new InputStreamReader(in, "UTF-8"));
        StringBuilder sb = new StringBuilder();
        char[] buf = new char[4096];
        int n;
        while ((n = reader.read(buf)) != -1) {
            sb.append(buf, 0, n);
        }
        reader.close();
        return sb.toString();
    }

    /** 带 HTTP 状态码的接口错误。 */
    public static final class ApiError extends Exception {
        public final int http;

        public ApiError(int http, String message) {
            super(message);
            this.http = http;
        }
    }
}
