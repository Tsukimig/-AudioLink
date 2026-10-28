package com.audiolink.receiver;

import android.app.*;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.os.*;
import org.json.JSONObject;
import java.io.IOException;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;

public final class ReceiverService extends Service {
    private static volatile ReceiverService instance;
    private static volatile String currentStatus="在电脑端点击「连接手机」，然后填入地址和连接码。";
    private static volatile float volume=1;
    private volatile boolean running, linked, streaming, nativePlaying, silentIdle, focused=true;
    private volatile byte[] session=new byte[16];
    private volatile long lastAudio, writtenFrames;
    private volatile AudioTrack track;
    private volatile int nativeFrames=960;
    private DatagramSocket socket;
    private Thread networkThread, playbackThread;
    private final JitterBuffer buffer=new JitterBuffer(12);
    private PowerManager.WakeLock wakeLock;
    private AudioManager audioManager;
    private AudioFocusRequest focusRequest;

    public static boolean isRunning() { ReceiverService s=instance;return s!=null && s.running; }
    public static String status() { return currentStatus; }
    public static float localVolume() { return volume; }
    public static void setLocalVolume(float value) {
        volume=Math.max(0,Math.min(1,value)); ReceiverService s=instance;
        if (s!=null && s.track!=null) try { s.track.setVolume(volume); } catch (IllegalStateException ignored) { }
    }
    public static boolean validAddress(String host) {
        String[] endpoint=host.split(":",-1);if (endpoint.length>2) return false;
        String[] parts=endpoint[0].split("\\.",-1);if (parts.length!=4) return false;
        try {
            if (endpoint.length==2 && (!endpoint[1].matches("[0-9]{1,5}") || Integer.parseInt(endpoint[1])<1 || Integer.parseInt(endpoint[1])>65535)) return false;
            for (String part:parts) { if (!part.matches("[0-9]{1,3}") || Integer.parseInt(part)>255) return false; }
            int first=Integer.parseInt(parts[0]);return first>0 && first<224;
        } catch (NumberFormatException ex) { return false; }
    }
    @Override public void onCreate() { super.onCreate();instance=this; }
    @Override public int onStartCommand(Intent intent,int flags,int startId) {
        if (intent==null || "stop".equals(intent.getAction())) { currentStatus="已断开";stopSelf();return START_NOT_STICKY; }
        if (running) return START_NOT_STICKY;
        String host=intent.getStringExtra("host"), code=intent.getStringExtra("code"), id=intent.getStringExtra("id");
        if (host==null || !validAddress(host) || code==null || !code.matches("[0-9]{6}") || id==null) { currentStatus="电脑地址或连接码不正确";stopSelf();return START_NOT_STICKY; }
        NotificationManager notifications=getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel("playback","手机音频接收",NotificationManager.IMPORTANCE_LOW));
        Intent stopIntent=new Intent(this,ReceiverService.class).setAction("stop");
        PendingIntent stop=PendingIntent.getService(this,1,stopIntent,PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent open=PendingIntent.getActivity(this,0,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        Notification notification=new Notification.Builder(this,"playback").setSmallIcon(R.drawable.ic_audio).setContentTitle("同频 AudioLink")
            .setContentText("正在接收电脑音频").setContentIntent(open).setOngoing(true).addAction(new Notification.Action.Builder(null,"断开",stop).build()).build();
        if (Build.VERSION.SDK_INT>=29) startForeground(1,notification,ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK);else startForeground(1,notification);
        running=true;currentStatus="正在连接电脑…";
        PowerManager power=(PowerManager)getSystemService(POWER_SERVICE);
        wakeLock=power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK,"AudioLink:receiver");wakeLock.setReferenceCounted(false);wakeLock.acquire();
        networkThread=new Thread(()->receive(host,code,id),"AudioLink network");networkThread.start();
        return START_NOT_STICKY;
    }
    private void createAudio() throws IOException {
        audioManager=(AudioManager)getSystemService(AUDIO_SERVICE);
        AudioAttributes attributes=new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build();
        focusRequest=new AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN).setAudioAttributes(attributes).setWillPauseWhenDucked(true)
            .setOnAudioFocusChangeListener(change->{ focused=change==AudioManager.AUDIOFOCUS_GAIN;
                if (!focused) { buffer.clear();currentStatus="播放已暂停，其他应用正在使用音频"; }
            },new Handler(Looper.getMainLooper())).build();
        focused=audioManager.requestAudioFocus(focusRequest)==AudioManager.AUDIOFOCUS_REQUEST_GRANTED;
        int minimum=AudioTrack.getMinBufferSize(Protocol.RATE,AudioFormat.CHANNEL_OUT_STEREO,AudioFormat.ENCODING_PCM_16BIT);
        if (minimum<=0) throw new IOException("手机不支持当前播放格式");
        AudioTrack created=new AudioTrack.Builder().setAudioAttributes(attributes)
            .setAudioFormat(new AudioFormat.Builder().setSampleRate(Protocol.RATE).setEncoding(AudioFormat.ENCODING_PCM_16BIT).setChannelMask(AudioFormat.CHANNEL_OUT_STEREO).build())
            .setTransferMode(AudioTrack.MODE_STREAM).setBufferSizeInBytes(Math.max(minimum,Protocol.BYTES*16)).setPerformanceMode(AudioTrack.PERFORMANCE_MODE_NONE).build();
        if (created.getState()!=AudioTrack.STATE_INITIALIZED) { created.release();throw new IOException("无法打开手机音频输出"); }
        created.setBufferSizeInFrames(960);
        // Use the system's regular media path and routing. Do not open a call /
        // receiver path or force the low-latency path, which can bypass effects.
        updateNativeBuffer(created);
        created.setVolume(volume);track=created;
    }
    private void updateNativeBuffer(AudioTrack output) {
        // Android may enlarge this buffer when the preferred speaker is routed,
        // or when it recreates a native track. Read the effective size again.
        nativeFrames=output.getBufferSizeInFrames();
        if (Build.VERSION.SDK_INT>=31) output.setStartThresholdInFrames(nativeFrames);
        buffer.setMinimum(Math.max(8,(nativeFrames+Protocol.FRAMES-1)/Protocol.FRAMES+4));
    }
    private void receive(String host,String pin,String id) {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_AUDIO);
        long lastControl=0,lastServer=SystemClock.elapsedRealtime(), disconnectedAt=lastServer;
        try {
            createAudio();
            String[] endpoint=host.split(":",-1);
            DatagramSocket connected=new DatagramSocket();socket=connected;connected.connect(InetAddress.getByName(endpoint[0]),endpoint.length==2?Integer.parseInt(endpoint[1]):Protocol.PORT);
            connected.setSoTimeout(100);connected.setReceiveBufferSize(64*1024);
            String model=Build.MODEL;
            if (model.startsWith("V2171")) model="iQOO 9";
            JSONObject hello=new JSONObject().put("id",id).put("code",pin).put("name",model)
                .put("latencyMs",buffer.targetPackets()*5).put("targetFrames",buffer.targetPackets()*Protocol.FRAMES);
            byte[] helloPayload=hello.toString().getBytes(StandardCharsets.UTF_8);
            byte[] data=new byte[1500];DatagramPacket packet=new DatagramPacket(data,data.length);
            Protocol.Header reusableHeader=new Protocol.Header();
            playbackThread=new Thread(this::play,"AudioLink playback");playbackThread.start();
            while (running) {
                long now=SystemClock.elapsedRealtime();
                if (now-lastControl>=(linked?500:800)) {
                    byte[] payload=linked ? new JSONObject().put("queueFrames",buffer.size()*Protocol.FRAMES+audioQueueFrames()).put("playing",nativePlaying)
                        .put("targetFrames",buffer.targetPackets()*Protocol.FRAMES)
                        .put("nativeBufferFrames",nativeFrames).put("nativeQueueFrames",audioQueueFrames())
                        .put("silentIdle",silentIdle).put("nativePlayState",track.getPlayState()).put("performanceMode",track.getPerformanceMode())
                        .put("underruns",buffer.losses()+track.getUnderrunCount()).toString().getBytes(StandardCharsets.UTF_8) : helloPayload;
                    send(Protocol.control(linked?Protocol.HEARTBEAT:Protocol.HELLO,session,payload));lastControl=now;
                }
                try {
                    packet.setLength(data.length);connected.receive(packet);
                    Protocol.Header header=Protocol.parse(data,packet.getLength(),reusableHeader);if (header==null) continue;
                    if (header.type==Protocol.WELCOME) {
                        JSONObject welcome=new JSONObject(new String(data,Protocol.HEADER,packet.getLength()-Protocol.HEADER,StandardCharsets.UTF_8));
                        if (welcome.optInt("rate")!=48000 || welcome.optInt("channels")!=2 || welcome.optInt("frames")!=240) throw new IOException("电脑与手机的音频版本不兼容");
                        boolean nonzero=false;for(byte b:header.session) nonzero|=b!=0;if (!nonzero) continue;
                        session=header.session.clone();linked=true;lastServer=now;currentStatus="已连接，等待电脑开始共享";
                    } else if (header.type==Protocol.REJECTED && !linked) {
                        JSONObject error=new JSONObject(new String(data,Protocol.HEADER,packet.getLength()-Protocol.HEADER,StandardCharsets.UTF_8));
                        currentStatus=error.optString("error","连接失败，请刷新电脑连接码");break;
                    } else if (Arrays.equals(session,header.session) && linked) {
                        lastServer=now;
                        if (header.type==Protocol.AUDIO) { buffer.offer(header.sequence,header.flags,header.framePosition,data,Protocol.HEADER);lastAudio=now;streaming=true; }
                        else if (header.type==Protocol.STOPPED) { streaming=false;buffer.clear();currentStatus="已连接，电脑已停止共享"; }
                    }
                } catch (SocketTimeoutException ignored) { }
                now=SystemClock.elapsedRealtime();
                if (linked && now-lastServer>4000) { linked=false;streaming=false;buffer.clear();disconnectedAt=now;currentStatus="连接中断，正在重新连接…"; }
                if (!linked && now-disconnectedAt>120000) { currentStatus="连接已断开，请重新连接";break; }
            }
        } catch (Exception ex) { if (running) currentStatus="连接失败："+(ex.getMessage()==null?ex.getClass().getSimpleName():ex.getMessage()); }
        finally {
            running=false;linked=false;streaming=false;nativePlaying=false;
            if (socket!=null) { try { send(Protocol.control(Protocol.GOODBYE,session,new byte[0])); } catch (Exception ignored) { } socket.close(); }
            if (playbackThread!=null) try { playbackThread.join(1500); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
            AudioTrack old=track;track=null;if (old!=null) { try { old.pause();old.flush(); } catch (IllegalStateException ignored) { } old.release(); }
            if (audioManager!=null && focusRequest!=null) audioManager.abandonAudioFocusRequest(focusRequest);
            if (wakeLock!=null && wakeLock.isHeld()) wakeLock.release();
            stopSelf();
        }
    }
    private int audioQueueFrames() {
        AudioTrack output=track;if (output==null) return 0;
        long queued=(writtenFrames-(output.getPlaybackHeadPosition()&0xffffffffL))&0xffffffffL;
        return queued>48000?0:(int)Math.min(queued,output.getBufferSizeInFrames());
    }
    private void send(byte[] packet) throws IOException { if (socket!=null && !socket.isClosed()) socket.send(new DatagramPacket(packet,packet.length)); }
    private void play() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
        byte[] pcm=new byte[Protocol.BYTES];boolean playing=false;
        PcmContinuity continuity=new PcmContinuity();PcmSilenceGate silence=new PcmSilenceGate();
        int lastUnderruns=0;long emptySince=0,stableSince=SystemClock.elapsedRealtime(),lastBufferCheck=0;
        try {
            while (running) {
                AudioTrack output=track;if (output==null) break;
                long now=SystemClock.elapsedRealtime();
                if (now-lastBufferCheck>=500) { updateNativeBuffer(output);lastBufferCheck=now; }
                if (!streaming || !focused || SystemClock.elapsedRealtime()-lastAudio>700) {
                    if (playing || writtenFrames>0) { output.pause();output.flush();writtenFrames=0;buffer.clear();playing=nativePlaying=false;continuity.reset();silence.reset(); }
                    silentIdle=false;
                    SystemClock.sleep(3);continue;
                }
                if (playing) {
                    if (audioQueueFrames()==0) { if (emptySince==0) emptySince=now; } else emptySince=0;
                    if (output.getUnderrunCount()>lastUnderruns || (emptySince!=0 && now-emptySince>12)) {
                        output.pause();output.flush();writtenFrames=0;playing=nativePlaying=false;continuity.reset();
                        buffer.grow();buffer.rebuffer();emptySince=0;stableSince=now;
                        currentStatus="正在恢复播放缓冲…";continue;
                    }
                    if (now-stableSince>20000) { buffer.relax();stableSince=now; }
                }
                int result=buffer.read(pcm);if (result==0) { SystemClock.sleep(2);continue; }
                if (silence.observe(pcm,volume==0)) {
                    if (playing || writtenFrames>0) { output.pause();output.flush();writtenFrames=0;playing=nativePlaying=false;continuity.reset(); }
                    silentIdle=true;emptySince=0;
                    // Continue receiving/draining while paused, keeping a safe
                    // reserve for immediate prefill when real audio resumes.
                    buffer.rebuffer();currentStatus="已连接，静音待机";continue;
                }
                silentIdle=false;continuity.process(pcm,result==2,buffer.resets());
                int offset=0;
                while (offset<pcm.length && running) {
                    int bytes=output.write(pcm,offset,pcm.length-offset,playing?AudioTrack.WRITE_BLOCKING:AudioTrack.WRITE_NON_BLOCKING);
                    if (bytes<0) throw new IOException("手机音频输出已断开（"+bytes+"）");
                    if (bytes==0) {
                        if (!playing && writtenFrames>0) { output.play();playing=nativePlaying=true;lastUnderruns=output.getUnderrunCount();stableSince=SystemClock.elapsedRealtime(); }
                        else SystemClock.sleep(1);
                        continue;
                    }
                    offset+=bytes;writtenFrames+=bytes/4;
                    // Prime the native queue while paused, then start once it
                    // actually contains audio, rather than starting an empty DAC.
                    if (!playing && writtenFrames>=nativeFrames) { output.play();playing=nativePlaying=true;lastUnderruns=output.getUnderrunCount();stableSince=SystemClock.elapsedRealtime(); }
                }
                if (focused && streaming) currentStatus="正在播放";
            }
        } catch (Exception ex) { if (running) { currentStatus="播放失败："+ex.getMessage();running=false;if (socket!=null) socket.close(); } }
    }
    @Override public void onDestroy() {
        running=false;streaming=false;
        try { send(Protocol.control(Protocol.GOODBYE,session,new byte[0])); } catch (IOException ignored) { }
        if (socket!=null) socket.close();stopForeground(STOP_FOREGROUND_REMOVE);
        if (instance==this) instance=null;super.onDestroy();
    }
    @Override public IBinder onBind(Intent intent) { return null; }
}
