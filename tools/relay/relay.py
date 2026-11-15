"""Shammy link relay: "link from anywhere" (docs/LINK.md §9 on the phone branch).

It only joins two TCP connections, the laptop and the phone of one pair, and copies bytes between them. It cannot
read them: everything inside is the same end-to-end encrypted link as on Wi-Fi (fresh keys per connection, identity
keys pinned at pairing), and pairing itself never happens through the relay. All it ever learns is a room id (a hash of
the two devices' public keys) and how many bytes pass.

Each side sends one line, then waits for "READY\n":
    SHAMMY-RELAY 1 <room: 32 hex> <laptop|phone>\n
A newer connection for the same room and role replaces the older one. A side that waits more than WAIT seconds alone,
or a pair silent for IDLE seconds, is dropped (the apps reconnect).

Run:  python relay.py [--host 0.0.0.0] [--port 47950]     (Python 3.9+, standard library only)
"""
import argparse
import asyncio
import re

WAIT = 600          # a laptop waits for its phone this long, then re-dials
IDLE = 180          # the link pings every 30 s, so a pair this quiet is gone
MAX_ROOMS = 5000
HELLO = re.compile(rb"^SHAMMY-RELAY 1 ([0-9a-f]{32}) (laptop|phone)\n$")

rooms = {}  # room -> {"laptop": (reader, writer, ready_event), "phone": ...}


async def pipe(src, dst):
    try:
        while True:
            data = await asyncio.wait_for(src.read(65536), IDLE)
            if not data:
                break
            dst.write(data)
            await dst.drain()
    except (asyncio.TimeoutError, ConnectionError, OSError):
        pass
    finally:
        dst.close()


async def handle(reader, writer):
    try:
        line = await asyncio.wait_for(reader.readline(), 10)
    except (asyncio.TimeoutError, ConnectionError, OSError):
        writer.close()
        return
    m = HELLO.match(line)
    if not m or (len(rooms) >= MAX_ROOMS and m.group(1) not in rooms):
        writer.close()
        return
    room, role = m.group(1).decode(), m.group(2).decode()
    other = "phone" if role == "laptop" else "laptop"
    slot = rooms.setdefault(room, {})
    if role in slot:  # a newer connection replaces the older one
        slot[role][1].close()
        slot[role][2].set()
    ready = asyncio.Event()
    slot[role] = (reader, writer, ready)

    if other in slot:
        o_reader, o_writer, o_ready = slot.pop(other)
        slot.pop(role, None)
        rooms.pop(room, None)
        for w in (writer, o_writer):
            w.write(b"READY\n")
        await asyncio.gather(writer.drain(), o_writer.drain(), return_exceptions=True)
        o_ready.set()
        await asyncio.gather(pipe(reader, o_writer), pipe(o_reader, writer))
        return

    try:
        await asyncio.wait_for(ready.wait(), WAIT)  # set when paired up (the other side runs the pipes) or replaced
    except asyncio.TimeoutError:
        if rooms.get(room, {}).get(role, (None, None, None))[2] is ready:
            rooms[room].pop(role, None)
            if not rooms[room]:
                rooms.pop(room, None)
        writer.close()


async def main():
    ap = argparse.ArgumentParser(description="Shammy link relay")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=47950)
    a = ap.parse_args()
    server = await asyncio.start_server(handle, a.host, a.port)
    print(f"Shammy relay on {a.host}:{a.port}", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
