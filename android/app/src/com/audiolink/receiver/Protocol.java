package com.audiolink.receiver;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.Arrays;

public final class Protocol {
    public static final int PORT=45891, HEADER=40, RATE=48000, FRAMES=240, BYTES=960;
    public static final int HELLO=1, WELCOME=2, HEARTBEAT=3, GOODBYE=4, AUDIO=5, STOPPED=6, REJECTED=7;
    public static final class Header {
        public int type, flags;
        public long sequence, framePosition;
        public byte[] session=new byte[16];
    }
    public static Header parse(byte[] packet, int length) {
        return parse(packet,length,new Header());
    }
    public static Header parse(byte[] packet, int length, Header reuse) {
        if (length<HEADER || length>packet.length || packet[0]!='A' || packet[1]!='L' || packet[2]!='N' || packet[3]!='K' || packet[4]!=1) return null;
        ByteBuffer b=ByteBuffer.wrap(packet,0,length).order(ByteOrder.LITTLE_ENDIAN);
        int type=packet[5]&255, frames=b.getShort(36)&65535, flags=b.getShort(38)&65535;
        if (type<1 || type>7 || (b.getShort(6)&65535)!=HEADER || (flags&~1)!=0) return null;
        if (type==AUDIO ? frames!=FRAMES || length!=HEADER+BYTES : frames!=0 || flags!=0 || length>HEADER+768) return null;
        Header h=reuse; h.type=type; h.flags=flags; System.arraycopy(packet,8,h.session,0,16);
        h.sequence=b.getInt(24)&0xffffffffL; h.framePosition=b.getLong(28);
        return h.framePosition<0 ? null : h;
    }
    public static byte[] control(int type, byte[] session, byte[] payload) {
        if (session.length!=16 || payload.length>768) throw new IllegalArgumentException("Invalid control packet");
        byte[] packet=new byte[HEADER+payload.length];
        ByteBuffer b=ByteBuffer.wrap(packet).order(ByteOrder.LITTLE_ENDIAN);
        b.put(new byte[]{'A','L','N','K',1,(byte)type}); b.putShort((short)HEADER); b.put(session);
        System.arraycopy(payload,0,packet,HEADER,payload.length);
        return packet;
    }
    private Protocol() { }
}
