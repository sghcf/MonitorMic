package com.example.micstreamer;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/**
 * 显示器重启后自动拉起麦克风服务器（v1.2.0 服务器模式无需目标列表）。
 * 注意：Android 12+ 对后台启动 microphone 类型前台服务有限制，
 * 若失败可由任意一台电脑端 App 通过 adb 重新拉起（try/catch 兜底不崩溃）。
 */
public class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "MicStreamer";

    @Override
    public void onReceive(Context ctx, Intent intent) {
        if (intent == null || !Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())) return;
        Log.i(TAG, "boot: starting mic server");
        try {
            ctx.startForegroundService(new Intent(ctx, MicService.class));
        } catch (Exception e) {
            Log.e(TAG, "boot: startForegroundService failed", e);
        }
    }
}
