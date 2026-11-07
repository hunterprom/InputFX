"""Minimal USBPcap (.pcap, linktype 249) parser: lists control transfers + vendor payloads."""
import struct, sys, collections

def read(path):
    d = open(path, 'rb').read()
    magic, vmaj, vmin, tz, sig, snap, link = struct.unpack_from('<IHHiIII', d, 0)
    assert link == 249, link
    off = 24
    while off + 16 <= len(d):
        sec, usec, incl, orig = struct.unpack_from('<IIII', d, off)
        off += 16
        pkt = d[off:off + incl]
        off += incl
        if len(pkt) < 27:
            continue
        hlen, irp, status, func, info, bus, dev, ep, ttype, dlen = struct.unpack_from('<HQIHBHHBBI', pkt, 0)
        stage = pkt[27] if ttype == 2 and len(pkt) > 27 else None
        payload = pkt[hlen:]
        yield dict(t=sec + usec / 1e6, bus=bus, dev=dev, ep=ep, ttype=ttype, info=info,
                   stage=stage, hdr=pkt[:hlen], payload=payload, dlen=dlen, status=status, irp=irp)

if __name__ == '__main__':
    path = sys.argv[1]
    t0 = None
    devs = collections.Counter()
    for p in read(path):
        devs[(p['bus'], p['dev'])] += 1
    print('devices (bus,addr):count', dict(devs))
