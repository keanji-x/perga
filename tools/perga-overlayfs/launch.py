#!/usr/bin/env python3
"""Run Perga and its mount helper in shared private Linux namespaces."""

import sys

if sys.platform != "linux":
    raise SystemExit("The Perga OverlayFS launcher requires Linux.")

import argparse
import ctypes
import errno
import fcntl
import json
import os
from pathlib import Path
import re
import select
import shutil
import signal
import subprocess
import tempfile
import time
from contextlib import ExitStack


LIBC = ctypes.CDLL(None, use_errno=True)
LIBC.mount.argtypes = [
    ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_ulong, ctypes.c_char_p
]
LIBC.umount2.argtypes = [ctypes.c_char_p, ctypes.c_int]
MOUNT_FLAGS = 2 | 4  # MS_NOSUID | MS_NODEV
DIRECTORY_FLAGS = os.O_PATH | os.O_DIRECTORY | os.O_NOFOLLOW
WORKSPACE_ID = re.compile(r"(?:trk|stage)_[0-9a-f]{32}\Z")
SNAPSHOT_ID = re.compile(r"[0-9a-f]{64}\Z")


def open_directory(stack, root, *parts):
    descriptor = os.open(root, DIRECTORY_FLAGS)
    stack.callback(os.close, descriptor)
    for part in parts:
        descriptor = os.open(part, DIRECTORY_FLAGS, dir_fd=descriptor)
        stack.callback(os.close, descriptor)
    return descriptor


def mount_overlay(root, lower, upper, work, merged):
    with ExitStack() as stack:
        lower_fd, upper_fd, work_fd, merged_fd = (
            open_directory(stack, root, *path.relative_to(root).parts)
            for path in (lower, upper, work, merged)
        )
        options = (
            f"lowerdir=/proc/self/fd/{lower_fd},upperdir=/proc/self/fd/{upper_fd},"
            f"workdir=/proc/self/fd/{work_fd},userxattr"
        )
        target = f"/proc/self/fd/{merged_fd}"
        if LIBC.mount(b"overlay", os.fsencode(target), b"overlay", MOUNT_FLAGS,
                      os.fsencode(options)) != 0:
            error = ctypes.get_errno()
            raise OSError(error, os.strerror(error))


def unmount(merged):
    if LIBC.umount2(os.fsencode(merged), 0) != 0:
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))


def current_mounts():
    with open("/proc/self/mountinfo", encoding="utf-8", errors="surrogateescape") as mounts:
        for line in mounts:
            fields, filesystem = line.rstrip("\n").split(" - ", 1)
            path = re.sub(r"\\([0-7]{3})", lambda item: chr(int(item[1], 8)), fields.split()[4])
            yield Path(path), filesystem.split()[0], fields.split()[0]


def checked_path(root, *segments):
    path = root.joinpath(*segments)
    if path.resolve() != path:
        raise ValueError("The managed workspace contains a symbolic link.")
    return path


class Mounts:
    def __init__(self, root):
        self.root = root
        self.tracks = {}

    def probe(self):
        workspace_root = checked_path(self.root)
        workspace_root.mkdir(mode=0o700, parents=True, exist_ok=True)
        directory = Path(tempfile.mkdtemp(prefix="probe-", dir=self.root))
        lower, upper, work, merged = (directory / name for name in ("lower", "upper", "work", "merged"))
        mounted = False
        try:
            for path in (lower, upper, work, merged):
                path.mkdir()
            (lower / "file").write_text("lower", encoding="utf-8")
            mount_overlay(self.root, lower, upper, work, merged)
            mounted = True
            (merged / "file").write_text("upper", encoding="utf-8")
            if (lower / "file").read_text(encoding="utf-8") != "lower" or not (upper / "file").is_file():
                raise ValueError("OverlayFS copy-up failed.")
        finally:
            if mounted:
                unmount(merged)
            internal_work = work / "work"
            if internal_work.is_dir():
                internal_work.chmod(0o700)
            shutil.rmtree(directory)

    def handle(self, request):
        operation = request.get("operation")
        if operation == "probe":
            self.probe()
            return

        track = request.get("trackId")
        if not isinstance(track, str) or not WORKSPACE_ID.fullmatch(track):
            raise ValueError("Invalid Track ID.")
        snapshot = request.get("snapshotId")
        if not isinstance(snapshot, str) or not SNAPSHOT_ID.fullmatch(snapshot):
            raise ValueError("Invalid snapshot ID.")

        track_path = checked_path(self.root, "tracks", track)
        merged = checked_path(track_path, "merged")
        live = [entry[1:] for entry in current_mounts() if entry[0] == merged]
        recorded = self.tracks.get(track)
        if live and (len(live) != 1 or recorded != (snapshot, live[0][1]) or live[0][0] != "overlay"):
            raise ValueError("The Track directory has an unexpected mount.")

        if operation == "unmount":
            # The Host removes the directory only after this check succeeds.
            if any(path != merged and (path == track_path or track_path in path.parents)
                   for path, _, _ in current_mounts()):
                raise OSError(errno.EBUSY, "The Track has other mounts.")
            if live:
                unmount(merged)
            self.tracks.pop(track, None)
            return
        if operation != "mount":
            raise ValueError("Unknown mount operation.")
        if live:
            return

        lower = checked_path(self.root, "snapshots", snapshot)
        upper = checked_path(self.root, "tracks", track, "upper")
        work = checked_path(self.root, "tracks", track, "work")
        mount_overlay(self.root, lower, upper, work, merged)
        for path, filesystem, mount_id in current_mounts():
            if path == merged and filesystem == "overlay":
                self.tracks[track] = (snapshot, mount_id)
                return
        raise ValueError("The new OverlayFS mount could not be verified.")


def serve(requests, replies, child, mounts):
    buffer = b""
    while child.poll() is None:
        if not select.select([requests], [], [], 0.25)[0]:
            continue
        data = os.read(requests, 4096)
        if not data:
            return child.wait()
        buffer += data
        while b"\n" in buffer:
            line, buffer = buffer.split(b"\n", 1)
            if len(line) > 4096:
                raise ValueError("Oversized mount request.")
            try:
                request = json.loads(line)
                if not isinstance(request, dict):
                    raise ValueError("Invalid mount request.")
                mounts.handle(request)
                result = {"ok": True}
            except (ValueError, OSError) as error:
                result = {"ok": False, "error": str(error)}
            os.write(replies, json.dumps(result).encode() + b"\n")
        if len(buffer) > 4096:
            raise ValueError("Oversized mount request.")
    return child.returncode


def launch(args):
    workspace_root = Path(args.workspaces).resolve()
    storage = checked_path(workspace_root, ".overlayfs")
    storage.mkdir(mode=0o700, parents=True, exist_ok=True)
    # Protect both the supervisor and its forked mount helper from same-UID peers.
    if LIBC.prctl(4, 0, 0, 0, 0) != 0:  # PR_SET_DUMPABLE
        raise OSError(ctypes.get_errno(), "Could not protect the mount helper.")
    # A workspace root belongs to one live launcher and its Host.
    lock_fd = os.open(storage / "launcher.lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        return supervise_namespace(args, workspace_root, storage, lock_fd)
    finally:
        os.close(lock_fd)


def supervise_namespace(args, workspace_root, storage, lock_fd):
    # Linux kills the remaining namespace processes when its PID 1 exits.
    # This releases inherited workspace mounts even after a Host crash.
    if LIBC.unshare(0x20000000) != 0:  # CLONE_NEWPID
        error = ctypes.get_errno()
        raise OSError(error, os.strerror(error))
    parent_read, parent_write = os.pipe()
    pid = os.fork()
    if pid == 0:
        os.close(parent_write)
        if LIBC.prctl(1, signal.SIGKILL, 0, 0, 0) != 0:  # PR_SET_PDEATHSIG
            raise OSError(ctypes.get_errno(), "Could not supervise the mount namespace.")
        if select.select([parent_read], [], [], 0)[0]:
            return 1  # The outer supervisor already exited.
        os.close(parent_read)
        return launch_host(args, workspace_root, storage, lock_fd)

    os.close(parent_read)
    pidfd = os.pidfd_open(pid)
    deadline = None

    def forward(signum, _frame):
        nonlocal deadline
        if deadline is None:
            deadline = time.monotonic() + 10
            try:
                signal.pidfd_send_signal(pidfd, signum)
            except ProcessLookupError:
                pass

    signal.signal(signal.SIGTERM, forward)
    signal.signal(signal.SIGINT, forward)
    try:
        while True:
            exited, status = os.waitpid(pid, os.WNOHANG)
            if exited:
                break
            if deadline is not None and time.monotonic() >= deadline:
                try:
                    signal.pidfd_send_signal(pidfd, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                _, status = os.waitpid(pid, 0)
                break
            time.sleep(0.1)
    finally:
        os.close(pidfd)
        os.close(parent_write)
    code = os.waitstatus_to_exitcode(status)
    return code if code >= 0 else 128 - code


def launch_host(args, workspace_root, storage, lock_fd):
    env = dict(os.environ)
    env["Perga__DataDir"] = str(Path(args.data_dir).resolve())
    env["Perga__WorkspacesRootDirectory"] = str(workspace_root)
    env["PERGA_OVERLAYFS_ROOT"] = str(workspace_root)
    requests, host_write = os.pipe()
    host_read, replies = os.pipe()
    env["PERGA_OVERLAYFS_READ_FD"] = str(host_read)
    env["PERGA_OVERLAYFS_WRITE_FD"] = str(host_write)
    env["PERGA_OVERLAYFS_LOCK_FD"] = str(lock_fd)
    try:
        child = subprocess.Popen(
            ["/usr/bin/setpriv", "--ambient-caps=-all", "--inh-caps=-all",
             "--bounding-set=-all", "--no-new-privs", "--pdeathsig", "SIGTERM", *args.command],
            env=env, pass_fds=(host_read, host_write, lock_fd), start_new_session=True,
        )
    finally:
        os.close(host_read)
        os.close(host_write)

    def forward(signum, _frame):
        if child.poll() is None:
            child.send_signal(signum)

    signal.signal(signal.SIGTERM, forward)
    signal.signal(signal.SIGINT, forward)
    try:
        status = serve(requests, replies, child, Mounts(storage))
    finally:
        os.close(requests)
        os.close(replies)
        if child.poll() is None:
            child.terminate()
            try:
                child.wait(timeout=10)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()
    return status if status >= 0 else 128 - status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--workspaces", required=True)
    parser.add_argument("--inside", action="store_true", help=argparse.SUPPRESS)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command and args.command[0] == "--":
        args.command.pop(0)
    if not args.command:
        parser.error("Specify the Perga Host command after --.")
    if os.getuid() == 0:
        parser.error("The OverlayFS launcher requires an unprivileged Linux user.")
    if not args.inside:
        command = ["/usr/bin/unshare", "--user", "--map-current-user", "--keep-caps",
                   "--mount", "--propagation", "private", sys.executable, __file__,
                   "--inside", *sys.argv[1:]]
        os.execv(command[0], command)
    try:
        return launch(args)
    except OSError as error:
        print(f"Perga OverlayFS launcher: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
