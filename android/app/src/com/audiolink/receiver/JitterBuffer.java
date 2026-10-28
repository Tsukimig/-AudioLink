package com.audiolink.receiver;

import java.util.Arrays;

/** Fixed memory, wrap-safe sequence handling. Late audio is never allowed to accumulate. */
public final class JitterBuffer {
    private static final int CAPACITY=64;
    private final byte[][] samples=new byte[CAPACITY][Protocol.BYTES];
    private final long[] sequences=new long[CAPACITY];
    private final boolean[] occupied=new boolean[CAPACITY];
    private int minimum=8, target;
    private long expected, newest, losses, resets;
    private long lastResetPosition=Long.MIN_VALUE;
    private int count;
    private boolean have, primed;
    public JitterBuffer(int targetPackets) { target=Math.max(2,Math.min(24,targetPackets));minimum=Math.min(minimum,target); }
    private static int distance(long value,long origin) { return (int)(value-origin); }
    private void clearCore() { Arrays.fill(occupied,false); count=0; have=false; primed=false; }
    public synchronized void clear() { clearCore();lastResetPosition=Long.MIN_VALUE; }
    public synchronized void rebuffer() { primed=false; }
    public synchronized void grow() { target=Math.min(24,target+2); }
    public synchronized void relax() { target=Math.max(minimum,target-1); }
    public synchronized void setMinimum(int packets) { minimum=Math.max(2,Math.min(24,packets));target=Math.max(target,minimum); }
    public synchronized int targetPackets() { return target; }
    public synchronized void offer(long sequence,int flags,byte[] packet,int offset) {
        offer(sequence,flags,sequence*Protocol.FRAMES,packet,offset);
    }
    public synchronized void offer(long sequence,int flags,long framePosition,byte[] packet,int offset) {
        sequence&=0xffffffffL;
        if (offset<0 || offset+Protocol.BYTES>packet.length) throw new IllegalArgumentException("Incomplete PCM");
        boolean newStream=(flags&1)!=0 && framePosition!=lastResetPosition;
        if (newStream && lastResetPosition==Long.MIN_VALUE && have && !primed && distance(sequence,expected)<=0 && distance(sequence,expected)>-CAPACITY) {
            lastResetPosition=framePosition;expected=sequence;newStream=false;
        }
        if (newStream) {
            if (have) resets++;
            clearCore();expected=newest=sequence;have=true;lastResetPosition=framePosition;
        }
        int existing=(int)sequence&(CAPACITY-1);
        if (occupied[existing] && sequences[existing]==sequence) return;
        if (have && !primed && distance(sequence,expected)<0 && distance(sequence,expected)>=-target) expected=sequence;
        if (!have || distance(sequence,expected)>=CAPACITY) {
            if (have) resets++;
            clearCore(); expected=newest=sequence; have=true;
        }
        if (distance(sequence,expected)<0) return;
        int slot=(int)sequence&(CAPACITY-1);
        if (occupied[slot] && sequences[slot]==sequence) return;
        if (!occupied[slot]) count++;
        occupied[slot]=true; sequences[slot]=sequence;
        System.arraycopy(packet,offset,samples[slot],0,Protocol.BYTES);
        if (distance(sequence,newest)>0) newest=sequence;
        // Jump to recent audio after a scheduling stall, instead of replaying stale sound.
        if (primed && distance(newest,expected)>target+8) {
            long keep=(newest-target+1)&0xffffffffL;
            while (distance(keep,expected)>0) { discardExpected(); expected=(expected+1)&0xffffffffL; }
            resets++;
        }
        notifyAll();
    }
    private void discardExpected() {
        int slot=(int)expected&(CAPACITY-1);
        if (occupied[slot] && sequences[slot]==expected) { occupied[slot]=false; count--; }
    }
    /** 0: wait for priming; 1: PCM; 2: a missing packet, concealed by the player. */
    public synchronized int read(byte[] output) {
        if (output.length<Protocol.BYTES) throw new IllegalArgumentException("Output too short");
        if (!have) return 0;
        if (!primed) {
            if (count<target) return 0;
            long keep=(newest-target+1)&0xffffffffL;
            if (count>target && distance(keep,expected)>0) {
                while (distance(keep,expected)>0) { discardExpected();expected=(expected+1)&0xffffffffL; }
                resets++;
            }
            primed=true;
        }
        int slot=(int)expected&(CAPACITY-1), result;
        if (occupied[slot] && sequences[slot]==expected) { System.arraycopy(samples[slot],0,output,0,Protocol.BYTES); discardExpected(); result=1; }
        else {
            // No later packet has arrived: wait. Advancing here makes every
            // subsequent real packet look late and turns one gap into a loop.
            if (distance(newest,expected)<0) return 0;
            Arrays.fill(output,(byte)0); losses++; result=2;
        }
        expected=(expected+1)&0xffffffffL;
        return result;
    }
    public synchronized int size() { return count; }
    public synchronized boolean isPrimed() { return primed; }
    public synchronized long losses() { return losses; }
    public synchronized long resets() { return resets; }
}
