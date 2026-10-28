package com.audiolink.receiver;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.InputFilter;
import android.text.InputType;
import android.view.View;
import android.widget.*;
import java.util.UUID;

public final class MainActivity extends Activity {
    private static final int TEXT=Color.rgb(228,238,223), SECONDARY=Color.rgb(158,181,157), ACCENT=Color.rgb(188,236,157);
    private EditText address, code;
    private TextView status, volumeText;
    private Button connect;
    private final Handler handler=new Handler(Looper.getMainLooper());
    private final Runnable refresh=new Runnable() { public void run() {
        status.setText(ReceiverService.status()); connect.setText(ReceiverService.isRunning() ? "断开连接" : "连接电脑");
        handler.postDelayed(this,500);
    }};
    private int dp(int value) { return (int)(getResources().getDisplayMetrics().density*value+0.5f); }
    private GradientDrawable panel(int color,int radius) { GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));return d; }
    private TextView label(String text,int size,int color) { TextView v=new TextView(this);v.setText(text);v.setTextSize(size);v.setTextColor(color);return v; }
    private void add(LinearLayout group,View view,int top,int height) { LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,height<0?height:dp(height));p.topMargin=dp(top);group.addView(view,p); }
    private EditText field(String hint,int type) { EditText v=new EditText(this);v.setSingleLine(true);v.setTextSize(17);v.setTextColor(TEXT);v.setHintTextColor(SECONDARY);v.setHint(hint);v.setInputType(type);v.setPadding(dp(14),dp(10),dp(14),dp(10));v.setBackground(panel(Color.rgb(25,39,28),10));return v; }
    private Button button(String text) { Button b=new Button(this);b.setText(text);b.setAllCaps(false);b.setTextSize(16);b.setTextColor(Color.rgb(32,53,27));b.setBackground(panel(ACCENT,12));return b; }
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);
        LinearLayout root=new LinearLayout(this);root.setOrientation(LinearLayout.VERTICAL);root.setPadding(dp(24),dp(28),dp(24),dp(28));root.setBackgroundColor(Color.rgb(16,23,18));scroll.addView(root);setContentView(scroll);
        TextView brand=label("同频  AudioLink",26,TEXT);brand.setTypeface(null,Typeface.BOLD);add(root,brand,0,-2);
        add(root,label("手机接收",17,ACCENT),12,-2);
        add(root,label("电脑地址",13,SECONDARY),32,-2);
        address=field("例如 192.168.42.100",InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);add(root,address,8,54);
        SharedPreferences prefs=getSharedPreferences("receiver",MODE_PRIVATE);address.setText(prefs.getString("address",""));
        add(root,label("连接码",13,SECONDARY),22,-2);
        code=field("电脑上显示的六位数字",InputType.TYPE_CLASS_NUMBER);code.setFilters(new InputFilter[]{new InputFilter.LengthFilter(6)});add(root,code,8,54);
        connect=button("连接电脑");add(root,connect,24,54);connect.setOnClickListener(v->toggle());
        status=label("在电脑端点击「连接手机」，然后填入地址和连接码。",14,SECONDARY);status.setLineSpacing(dp(4),1);add(root,status,18,-2);
        LinearLayout volumeRow=new LinearLayout(this);volumeRow.setOrientation(LinearLayout.HORIZONTAL);
        TextView volumeLabel=label("手机播放音量",14,TEXT);volumeRow.addView(volumeLabel,new LinearLayout.LayoutParams(0,-2,1));
        volumeText=label(Math.round(ReceiverService.localVolume()*100)+"%",14,ACCENT);volumeRow.addView(volumeText);add(root,volumeRow,30,-2);
        SeekBar volume=new SeekBar(this);volume.setMax(100);volume.setProgress(Math.round(ReceiverService.localVolume()*100));add(root,volume,8,44);
        volume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            public void onProgressChanged(SeekBar bar,int value,boolean user) { volumeText.setText(value+"%");ReceiverService.setLocalVolume(value/100f); }
            public void onStartTrackingTouch(SeekBar bar) { } public void onStopTrackingTouch(SeekBar bar) { }
        });
        Button usb=new Button(this);usb.setText("打开 USB 网络共享设置");usb.setAllCaps(false);usb.setTextColor(TEXT);usb.setBackground(panel(Color.rgb(36,54,38),10));add(root,usb,22,50);
        usb.setOnClickListener(v->{ try { startActivity(new Intent("android.settings.TETHER_SETTINGS")); } catch (Exception e) { try { startActivity(new Intent("android.settings.WIRELESS_SETTINGS")); } catch (Exception ignored) { Toast.makeText(this,"请在系统设置中搜索 USB 网络共享",Toast.LENGTH_LONG).show(); } } });
    }
    private void toggle() {
        if (ReceiverService.isRunning()) { stopService(new Intent(this,ReceiverService.class));return; }
        String host=address.getText().toString().trim(), pin=code.getText().toString().trim();
        if (!ReceiverService.validAddress(host)) { address.setError("填入电脑显示的 IPv4 地址");return; }
        if (!pin.matches("[0-9]{6}")) { code.setError("填入六位连接码");return; }
        SharedPreferences prefs=getSharedPreferences("receiver",MODE_PRIVATE);
        String id=prefs.getString("id",null);if (id==null) { id=UUID.randomUUID().toString();prefs.edit().putString("id",id).apply(); }
        prefs.edit().putString("address",host).apply();
        if (Build.VERSION.SDK_INT>=33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},1);
        Intent intent=new Intent(this,ReceiverService.class).putExtra("host",host).putExtra("code",pin).putExtra("id",id);
        startForegroundService(intent);status.setText("正在连接电脑…");
    }
    @Override protected void onResume() { super.onResume();handler.post(refresh); }
    @Override protected void onPause() { handler.removeCallbacks(refresh);super.onPause(); }
}
