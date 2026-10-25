package com.ln334.zhifuwater;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONObject;

/**
 * 登录页：服务器地址 + 账号 + 密码，登录成功保存令牌并进入主界面。
 */
public class LoginActivity extends Activity {

    public static final String EXTRA_SERVER = "extra_server";

    private EditText serverInput;
    private EditText userInput;
    private EditText passInput;
    private TextView errorView;
    private TextView loginButton;
    private ProgressBar progress;
    private boolean loading;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Api.onUnauthorized = null;

        Session session = Session.of(this);
        String forcedServer = getIntent() == null ? null : getIntent().getStringExtra(EXTRA_SERVER);
        if (session.isLoggedIn() && forcedServer == null) {
            goMain();
            return;
        }
        buildUi(session, forcedServer);
    }

    private void buildUi(Session session, String forcedServer) {
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        scroll.setBackgroundColor(U.BG);

        LinearLayout page = U.linear(this, LinearLayout.VERTICAL);
        page.setPadding(U.dp(this, 22), U.dp(this, 40), U.dp(this, 22), U.dp(this, 28));

        // 品牌区
        FrameLayout logoBox = new FrameLayout(this);
        logoBox.setBackground(U.roundBg(this, U.BRAND_BG, 18));
        GlyphView drop = U.glyph(this, GlyphView.DROP, 34, U.BRAND);
        FrameLayout.LayoutParams dropLp = new FrameLayout.LayoutParams(U.dp(this, 34), U.dp(this, 34));
        dropLp.gravity = Gravity.CENTER;
        logoBox.addView(drop, dropLp);
        page.addView(logoBox, U.lp(U.dp(this, 64), U.dp(this, 64)));

        TextView appName = U.tv(this, "智赋乡水", 26, U.INK, true);
        page.addView(appName, U.lpWith(this, U.MATCH, U.WRAP, 0, 18, 0, 0));
        TextView sub = U.tv(this, "智慧水处理平台 · 移动端", 13, U.MUTED, false);
        page.addView(sub, U.lpWith(this, U.MATCH, U.WRAP, 0, 5, 0, 0));
        TextView welcome = U.tv(this, "登录后查看站点运行、告警、工单与巡检", 12, U.MUTED2, false);
        page.addView(welcome, U.lpWith(this, U.MATCH, U.WRAP, 0, 14, 0, 0));

        // 表单卡片
        LinearLayout card = U.card(this);
        U.pad(card, this, 18, 16, 18, 18);
        page.addView(card, U.lpWith(this, U.MATCH, U.WRAP, 0, 22, 0, 0));

        serverInput = U.input(this, "http://电脑IP:8765", serverUrlText(session, forcedServer),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI, false);
        addField(card, "服务器地址", serverInput, 0);

        userInput = U.input(this, "请输入账号", session.username(),
                InputType.TYPE_CLASS_TEXT, false);
        addField(card, "账号", userInput, 14);

        passInput = U.input(this, "请输入密码", "",
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD, false);
        addField(card, "密码", passInput, 14);

        errorView = U.tv(this, "", 12.5f, U.DANGER, false);
        errorView.setVisibility(View.GONE);
        errorView.setLineSpacing(U.dp(this, 2), 1f);
        card.addView(errorView, U.lpWith(this, U.MATCH, U.WRAP, 0, 10, 0, 0));

        progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        progress.setIndeterminate(true);
        progress.setVisibility(View.GONE);
        card.addView(progress, U.lpWith(this, U.MATCH, U.dp(this, 3), 0, 12, 0, 4));

        loginButton = U.primaryButton(this, "登 录");
        loginButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                doLogin();
            }
        });
        card.addView(loginButton, U.lpWith(this, U.MATCH, U.dp(this, 50), 0, 18, 0, 0));

        TextView footer = U.tv(this, "平台演示账号 admin / water123", 11.5f, U.MUTED2, false);
        footer.setGravity(Gravity.CENTER);
        page.addView(footer, U.lpWith(this, U.MATCH, U.WRAP, 0, 18, 0, 0));

        TextView version = U.tv(this, "v1.0.0 · 原生安卓客户端", 10.5f, U.MUTED2, false);
        version.setGravity(Gravity.CENTER);
        page.addView(version, U.lpWith(this, U.MATCH, U.WRAP, 0, 6, 0, 0));

        scroll.addView(page, new ScrollView.LayoutParams(U.MATCH, U.WRAP));
        setContentView(scroll);
    }

    private String serverUrlText(Session session, String forcedServer) {
        if (forcedServer != null) return forcedServer;
        return session.baseUrl();
    }

    private void addField(LinearLayout parent, String label, EditText input, int topMargin) {
        TextView l = U.tv(this, label, 12, U.MUTED, true);
        parent.addView(l, U.lpWith(this, U.MATCH, U.WRAP, 0, topMargin, 0, 6));
        parent.addView(input, U.lp(U.MATCH, U.WRAP));
    }

    private void showError(String msg) {
        if (errorView == null) return;
        if (msg == null || msg.length() == 0) {
            errorView.setVisibility(View.GONE);
            return;
        }
        errorView.setText(msg);
        errorView.setVisibility(View.VISIBLE);
    }

    private void setLoading(boolean value) {
        loading = value;
        if (progress != null) progress.setVisibility(value ? View.VISIBLE : View.GONE);
        if (loginButton != null) {
            loginButton.setEnabled(!value);
            loginButton.setText(value ? "登录中…" : "登 录");
        }
    }

    private void doLogin() {
        if (loading) return;
        final String base = Session.normalizeBase(serverInput.getText().toString());
        final String user = userInput.getText().toString().trim();
        final String pass = passInput.getText().toString();
        if (base.length() == 0) {
            showError("请填写服务器地址，例如 http://192.168.1.10:8765");
            serverInput.requestFocus();
            return;
        }
        if (user.length() == 0) {
            showError("请填写账号");
            userInput.requestFocus();
            return;
        }
        if (pass.length() == 0) {
            showError("请填写密码");
            passInput.requestFocus();
            return;
        }
        showError(null);
        U.hideKeyboard(this);
        setLoading(true);
        final String oldBase = Session.of(this).baseUrl();
        Api.login(this, base, user, pass, new Api.Cb() {
            @Override
            public void done(JSONObject root, String error) {
                setLoading(false);
                if (error != null) {
                    showError(error);
                    return;
                }
                JSONObject data = root == null ? null : root.optJSONObject("data");
                String token = data == null ? "" : data.optString("token", "");
                if (token.length() == 0) {
                    showError("登录响应缺少令牌，请确认服务器地址是否正确");
                    return;
                }
                JSONObject u = data.optJSONObject("user");
                Session s = Session.of(LoginActivity.this);
                s.setBaseUrl(base);
                s.setToken(token);
                s.setUsername(user);
                s.setUser(u);
                if (!base.equals(oldBase)) {
                    s.setSite(Session.DEFAULT_SITE_ID, Session.DEFAULT_SITE_NAME);
                }
                String name = u == null ? user : u.optString("name", user);
                U.toast(LoginActivity.this, "登录成功，欢迎 " + name);
                goMain();
            }
        });
    }

    private void goMain() {
        Intent intent = new Intent(this, MainActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP);
        startActivity(intent);
        finish();
    }
}
