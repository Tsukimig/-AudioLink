package com.audiolink.receiver;

/** Leave steady PCM untouched; fade only at startup and real discontinuities. */
public final class PcmContinuity {
    private int left,right;
    private long resetMarker;
    private boolean fadeIn=true;
    public void reset() { left=right=0;fadeIn=true; }
    private static int sample(byte[] pcm,int offset) { return (short)((pcm[offset]&255)|(pcm[offset+1]<<8)); }
    private static void put(byte[] pcm,int offset,int value) { pcm[offset]=(byte)value;pcm[offset+1]=(byte)(value>>8); }
    public void process(byte[] pcm,boolean missing,long marker) {
        if (pcm.length!=Protocol.BYTES) throw new IllegalArgumentException("Incomplete PCM");
        if (missing || fadeIn || marker!=resetMarker) {
            int ramp=missing?Protocol.FRAMES:48;
            for (int frame=0;frame<ramp;frame++) {
                int i=frame*4;
                float mix=(frame+1f)/ramp;
                put(pcm,i,(int)(left*(1-mix)+sample(pcm,i)*mix));
                put(pcm,i+2,(int)(right*(1-mix)+sample(pcm,i+2)*mix));
            }
        }
        resetMarker=marker;fadeIn=missing;
        left=sample(pcm,pcm.length-4);right=sample(pcm,pcm.length-2);
    }
}
