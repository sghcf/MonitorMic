package com.example.micstreamer;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.Gravity;
import android.widget.TextView;

public class MainActivity extends Activity {
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        TextView tv = new TextView(this);
        tv.setText("MicStreamer v1.2.0\n\n麦克风服务器运行中，端口 50010\n任何设备连接即推送音频（可多机同时）\n\n关闭小爱远场唤醒后，\n本应用独占四麦克风阵列。");
        tv.setTextSize(20);
        tv.setTextColor(Color.WHITE);
        tv.setBackgroundColor(Color.rgb(0x1a, 0x1a, 0x2e));
        tv.setGravity(Gravity.CENTER);
        setContentView(tv);

        startForegroundService(new Intent(this, MicService.class));
        // 启动服务后立刻退出界面，不占用显示器画面
        finish();
    }
}
