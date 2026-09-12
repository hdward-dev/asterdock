#!/usr/bin/env python3
"""Local scrcpy protocol fixture; never connects to a real device."""
import os, pathlib, re, socket, struct, sys, threading, time
root = pathlib.Path(__file__).parent
args = sys.argv[1:]
if args[:1] == ['-s']: args = args[2:]
if args[:1] == ['devices']:
    print('List of devices attached\nfixture device model:Test_Phone')
elif args[:1] == ['forward']:
    if '--remove' in args: (root / 'removed').write_text('yes')
    else: print((root / 'port').read_text())
elif args[:1] == ['push']: print('1 file pushed')
elif 'app_process' in args:
    listener = socket.socket()
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(('127.0.0.1', int((root / 'port').read_text())))
    listener.listen(2)
    video, _ = listener.accept(); video.sendall(b'\0')
    control, _ = listener.accept(); listener.close()
    if (root / 'delay-handshake').exists(): time.sleep(20)
    name = b'Test Phone'.ljust(64, b'\0')
    video.sendall(name[:7]); time.sleep(.02); video.sendall(name[7:])
    def packet(data, config=False):
        head = struct.pack('>QI', (1 << 63) if config else 1234, len(data))
        video.sendall(head[:3]); video.sendall(head[3:] + data)
    def stream():
        try:
            for name in ['landscape.h264', 'portrait.h264']:
                data = (root / name).read_bytes()
                units = [b'\0\0\0\1' + x for x in re.split(b'\x00\x00\x00?\x01', data) if x]
                config = b''.join(x for x in units if x[4] & 31 in (7, 8))
                packet(config, True)
                for _ in range(3):
                    for unit in units:
                        if unit[4] & 31 in (7, 8): continue
                        packet(unit)
                        if unit[4] & 31 in (1, 5): time.sleep(.04)
            while not (root / 'disconnect').exists(): time.sleep(.05)
        except OSError: pass
        finally:
            video.close()
            try: control.shutdown(socket.SHUT_RDWR)
            except OSError: pass
            control.close()
    threading.Thread(target=stream, daemon=True).start()
    def read(n):
        data = b''
        while len(data) < n:
            part = control.recv(n-len(data))
            if not part: raise EOFError()
            data += part
        return data
    try:
        while True:
            tag = read(1); kind = tag[0]
            sizes = {0:13, 2:31, 3:20, 8:1, 11:0}
            rest = read(sizes[kind])
            with (root / 'commands').open('a') as out: out.write((tag+rest).hex()+'\n')
            if kind == 8:
                text = '来自手机'.encode()
                control.sendall(b'\0' + struct.pack('>I',len(text)) + text)
    except (EOFError, OSError): pass
elif args[:2] == ['shell', 'rm']: (root / 'cleaned').write_text('yes')
