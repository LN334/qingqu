package com.ln334.zhifuwater;

import android.app.Activity;
import android.os.Bundle;
import android.util.TypedValue;
import android.widget.TextView;

/** 构建链自检用占位页面，稍后替换为正式登录页。 */
public class LoginActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TextView tv = new TextView(this);
        tv.setText("智赋乡水");
        tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, 24);
        tv.setGravity(android.view.Gravity.CENTER);
        setContentView(tv);
    }
}
