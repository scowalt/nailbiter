import collections
import resource
import socket
import sys
import time


def listen_on_ephemeral_port():
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.bind(("0.0.0.0", 0))
    listener.listen()
    return listener


def main():
    held_count = int(sys.argv[1])
    churn_per_second = int(sys.argv[2])
    _, hard = resource.getrlimit(resource.RLIMIT_NOFILE)
    resource.setrlimit(resource.RLIMIT_NOFILE, (hard, hard))
    held = collections.deque(listen_on_ephemeral_port() for _ in range(held_count))
    print(f"holding {len(held)} ephemeral listeners, churning {churn_per_second}/s", flush=True)
    interval = 1 / churn_per_second if churn_per_second else None
    while True:
        if interval is None:
            time.sleep(3600)
            continue
        held.popleft().close()
        held.append(listen_on_ephemeral_port())
        time.sleep(interval)


main()
