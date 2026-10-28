package com.audiolink.receiver;

import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

public final class ReceiverTests {
    private static void check(boolean condition,String label) { if (!condition) throw new AssertionError(label); }
    private static byte[] pcm(long sequence) { byte[] data=new byte[Protocol.BYTES];for(int i=0;i<data.length;i++) data[i]=(byte)sequence;return data; }
    private static void offer(JitterBuffer b,long sequence,int flags,long position) { b.offer(sequence,flags,position,pcm(sequence),0); }
    private static void expect(JitterBuffer b,byte[] out,long sequence) { check(b.read(out)==1,"missing PCM "+sequence);check((out[0]&255)==(sequence&255),"wrong PCM "+sequence); }
    private static void noFutureIsNotLoss() {
        JitterBuffer b=new JitterBuffer(4);byte[] out=new byte[Protocol.BYTES];
        for(int i=0;i<4;i++) offer(b,i,i==0?1:0,10000+i*240);
        for(int i=0;i<4;i++) expect(b,out,i);
        for(int i=0;i<100;i++) check(b.read(out)==0,"empty queue advanced into the future");
        check(b.losses()==0,"waiting was counted as loss");offer(b,4,0,10960);expect(b,out,4);
    }
    private static void reorderAndLoss() {
        JitterBuffer b=new JitterBuffer(4);byte[] out=new byte[Protocol.BYTES];
        offer(b,1,0,10240);offer(b,0,1,10000);offer(b,3,0,10720);offer(b,1,0,10240);offer(b,4,0,10960);
        expect(b,out,0);expect(b,out,1);check(b.read(out)==2,"hole was not concealed");
        for(byte x:out) check(x==0,"concealment contains unrelated audio");
        expect(b,out,3);expect(b,out,4);check(b.losses()==1,"wrong loss count");
    }
    private static void restartAndWrap() {
        JitterBuffer b=new JitterBuffer(4);byte[] out=new byte[Protocol.BYTES];
        for(int i=0;i<4;i++) offer(b,(0xfffffffeL+i)&0xffffffffL,i==0?1:0,10000+i*240);
        for(int i=0;i<4;i++) expect(b,out,(0xfffffffeL+i)&0xffffffffL);
        for(int i=0;i<4;i++) offer(b,i,i==0?1:0,90000+i*240);
        for(int i=0;i<4;i++) expect(b,out,i);
    }
    private static void boundedAndAdaptive() {
        JitterBuffer b=new JitterBuffer(12);byte[] out=new byte[Protocol.BYTES];
        for(int i=0;i<20000;i++) { offer(b,i,i==0?1:0,10000+i*240);check(b.size()<=64,"unbounded queue"); }
        check(b.read(out)==1,"failed to recover after a stall");
        check((out[0]&255)>=(19988&255),"replayed old backlog");
        for(int i=0;i<100;i++) b.grow();check(b.targetPackets()==24,"adaptive upper bound");
        b.setMinimum(8);for(int i=0;i<100;i++) b.relax();check(b.targetPackets()==8,"adaptive lower bound");
    }
    private static void usbBursts() {
        JitterBuffer b=new JitterBuffer(12);byte[] out=new byte[Protocol.BYTES];
        TreeMap<Integer,ArrayList<Integer>> arrivals=new TreeMap<Integer,ArrayList<Integer>>();
        for(int seq=0;seq<12000;seq++) {
            int sourceTime=seq*5;
            int group=(sourceTime+19)/20*20;
            int arrival=group+((group/1000)%4==1?14:0);
            ArrayList<Integer> list=arrivals.get(arrival);if(list==null) { list=new ArrayList<Integer>();arrivals.put(arrival,list); }list.add(seq);
        }
        ArrayDeque<Integer> nativeQueue=new ArrayDeque<Integer>();boolean playing=false;int nextPlay=0, played=0, misses=0,firstSequence=0;
        for(int ms=0;ms<60000;ms++) {
            ArrayList<Integer> delivered=arrivals.remove(ms);
            if(delivered!=null) for(int seq:delivered) offer(b,seq,seq==0?1:0,10000+seq*240);
            if(playing && ms>=nextPlay) {
                Integer value=nativeQueue.poll();if(value==null) misses++;else { check(value==((firstSequence+played)&255),"USB burst changed sequence at "+ms+" ms");played++; }nextPlay+=5;
            }
            while(nativeQueue.size()<4) { int result=b.read(out);if(result==0) break;check(result==1,"coalesced USB delivery was treated as a lost packet");nativeQueue.add(out[0]&255); }
            if(!playing && nativeQueue.size()==4) { firstSequence=nativeQueue.peek();playing=true;nextPlay=ms+5; }
        }
        check(played>11800 && misses==0,"USB burst simulation underruns="+misses+", played="+played);
    }
    private static void protocolBounds() {
        byte[] packet=Protocol.control(Protocol.HEARTBEAT,new byte[16],new byte[0]);check(Protocol.parse(packet,packet.length)!=null,"valid header rejected");
        for(int size=0;size<Protocol.HEADER;size++) check(Protocol.parse(packet,size)==null,"truncated header accepted");
        packet[4]=2;check(Protocol.parse(packet,packet.length)==null,"unknown wire version accepted");
        packet[4]=1;packet[38]=2;check(Protocol.parse(packet,packet.length)==null,"unknown flags accepted");
    }
    private static byte[] tonePacket(int packet) {
        byte[] pcm=new byte[Protocol.BYTES];
        for (int frame=0;frame<Protocol.FRAMES;frame++) {
            int value=(int)(12000*Math.sin(2*Math.PI*997*(packet*Protocol.FRAMES+frame)/Protocol.RATE));
            for(int channel=0;channel<2;channel++) { int i=frame*4+channel*2;pcm[i]=(byte)value;pcm[i+1]=(byte)(value>>8); }
        }
        return pcm;
    }
    private static void steadyWaveform() {
        PcmContinuity continuity=new PcmContinuity();
        for(int packet=0;packet<1000;packet++) {
            byte[] expected=tonePacket(packet),actual=expected.clone();continuity.process(actual,false,0);
            int first=packet==0?48*4:0;
            for(int i=first;i<actual.length;i++) check(actual[i]==expected[i],"steady PCM was modified at packet "+packet);
        }
    }
    private static void digitalSilence() {
        PcmSilenceGate gate=new PcmSilenceGate();byte[] zero=new byte[Protocol.BYTES];
        for(int i=0;i<59;i++) check(!gate.observe(zero,false),"silence hold too short");
        check(gate.observe(zero,false),"digital silence did not suspend");
        byte[] quiet=zero.clone();quiet[0]=1;
        for(int i=0;i<1000;i++) check(!gate.observe(quiet,false),"a quiet nonzero sample was gated");
        check(gate.observe(tonePacket(1),true),"local zero volume did not suspend");
        check(!gate.observe(tonePacket(2),false),"audio did not resume immediately");
    }
    private static void lossRecoveryFade() {
        PcmContinuity continuity=new PcmContinuity();
        continuity.process(tonePacket(1),false,0);
        byte[] loss=new byte[Protocol.BYTES];continuity.process(loss,true,0);
        check(loss[loss.length-4]==0 && loss[loss.length-3]==0,"lost packet did not end at silence");
        byte[] resumed=tonePacket(3),original=resumed.clone();continuity.process(resumed,false,0);
        int initial=(short)((resumed[0]&255)|(resumed[1]<<8));check(Math.abs(initial)<300,"resume jumped straight from zero");
        for(int i=48*4;i<resumed.length;i++) check(resumed[i]==original[i],"loss fade altered later PCM");
    }
    private static void network(String host,int port,String code) throws Exception {
        DatagramSocket socket=new DatagramSocket();socket.connect(InetAddress.getByName(host),port);socket.setSoTimeout(300);
        String hello="{\"id\":\""+UUID.randomUUID()+"\",\"name\":\"Java Android receiver\",\"code\":\""+code+"\",\"latencyMs\":60,\"targetFrames\":2880}";
        byte[] session=new byte[16],packetBuffer=new byte[1500];
        Protocol.Header reuse=new Protocol.Header();JitterBuffer jitter=new JitterBuffer(12);byte[] out=new byte[Protocol.BYTES];
        long lastControl=0, end=System.nanoTime()+180_000_000_000L;boolean connected=false;long frames=0;
        try {
            while(System.nanoTime()<end) {
                long now=System.nanoTime();
                if(now-lastControl>300_000_000L) {
                    byte[] payload=(connected?"{\"playing\":true,\"queueFrames\":2880,\"targetFrames\":2880}":hello).getBytes(StandardCharsets.UTF_8);
                    byte[] control=Protocol.control(connected?Protocol.HEARTBEAT:Protocol.HELLO,session,payload);socket.send(new DatagramPacket(control,control.length));lastControl=now;
                }
                DatagramPacket p=new DatagramPacket(packetBuffer,packetBuffer.length);
                try { socket.receive(p); } catch(SocketTimeoutException ignored) { continue; }
                Protocol.Header h=Protocol.parse(packetBuffer,p.getLength(),reuse);check(h!=null,"invalid Windows packet");
                if(h.type==Protocol.WELCOME) { session=h.session.clone();connected=true; }
                else if(h.type==Protocol.AUDIO) {
                    check(Arrays.equals(h.session,session),"session mismatch");jitter.offer(h.sequence,h.flags,h.framePosition,packetBuffer,Protocol.HEADER);
                    while(jitter.size()>11) { if(jitter.read(out)==0) break;frames+=240; }
                } else if(h.type==Protocol.STOPPED) break;
            }
            check(connected && frames>48000,"Windows to Java audio did not arrive");
            check(jitter.losses()==0,"Windows to Java packet loss="+jitter.losses());
            System.out.println("PASS Java Android wire receiver frames="+frames+", losses="+jitter.losses());
            byte[] bye=Protocol.control(Protocol.GOODBYE,session,new byte[0]);socket.send(new DatagramPacket(bye,bye.length));
        } finally { socket.close(); }
    }
    public static void main(String[] args) throws Exception {
        if(args.length==4 && args[0].equals("network")) { network(args[1],Integer.parseInt(args[2]),args[3]);return; }
        Runnable[] tests={ReceiverTests::noFutureIsNotLoss,ReceiverTests::reorderAndLoss,ReceiverTests::restartAndWrap,ReceiverTests::boundedAndAdaptive,ReceiverTests::usbBursts,ReceiverTests::protocolBounds,ReceiverTests::steadyWaveform,ReceiverTests::digitalSilence,ReceiverTests::lossRecoveryFade};
        String[] names={"empty queue preserves future packets","reorder, duplicates and loss","stream restart and sequence wrap","bounded backlog and adaptive limits","60 seconds USB bursts and native prefill","protocol bounds","continuous PCM is bit-identical after startup fade","digital silence suspends without gating quiet audio","loss recovery fades in once"};
        for(int i=0;i<tests.length;i++) { tests[i].run();System.out.println("PASS "+names[i]); }
        System.out.println(tests.length+"/"+tests.length+" Android receiver tests passed");
    }
}
