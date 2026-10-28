package com.audiolink.receiver;

/** Suspend the output after digital silence; never gate a quiet nonzero sample. */
public final class PcmSilenceGate {
    private static final int HOLD_FRAMES=Protocol.RATE*300/1000;
    private int quietFrames;
    public void reset() { quietFrames=0; }
    public boolean observe(byte[] pcm,boolean muted) {
        if (pcm.length!=Protocol.BYTES) throw new IllegalArgumentException("Incomplete PCM");
        if (muted) { quietFrames=HOLD_FRAMES;return true; }
        for (byte sample:pcm) if (sample!=0) { quietFrames=0;return false; }
        quietFrames=Math.min(HOLD_FRAMES,quietFrames+Protocol.FRAMES);
        return quietFrames>=HOLD_FRAMES;
    }
}
