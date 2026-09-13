#!/usr/bin/env python3
"""Run one SMAPI process group with bounded lifetime, tee logging, and cleanup."""

from __future__ import annotations

import argparse
import datetime as dt
import os
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Callable, Mapping


TIMEOUT_EXIT = 124
INTERRUPTED_EXIT = 130
START_FAILURE_EXIT = 126
TEARDOWN_FAILURE_EXIT = 125


def _group_exists(process_group: int) -> bool:
    try:
        os.killpg(process_group, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _group_has_live_members(process_group: int) -> bool:
    return _group_exists(process_group)


def _signal_group(process_group: int, requested: signal.Signals) -> str | None:
    try:
        os.killpg(process_group, requested)
        return None
    except ProcessLookupError:
        return None
    except PermissionError as error:
        return f"Unable to signal owned process group {process_group} with {requested.name}: {error}"


def _retire_group(process_group: int, grace_seconds: float) -> list[str]:
    errors: list[str] = []
    if not _group_has_live_members(process_group):
        return errors
    term_error = _signal_group(process_group, signal.SIGTERM)
    if term_error:
        errors.append(term_error)
    deadline = time.monotonic() + grace_seconds
    while time.monotonic() < deadline:
        if not _group_has_live_members(process_group):
            return errors
        time.sleep(0.05)
    error = _signal_group(process_group, signal.SIGKILL)
    # On macOS a group containing only already-terminated zombies can return EPERM for SIGKILL.
    # A successful TERM followed by that condition is not an unretired live process.
    if error and term_error:
        errors.append(error)
    return errors


def _pump(stream, log) -> None:
    output = getattr(sys.stdout, "buffer", sys.stdout)
    read = getattr(stream, "read1", stream.read)
    while True:
        chunk = read(65536)
        if not chunk:
            return
        log.write(chunk)
        log.flush()
        try:
            output.write(chunk)
            output.flush()
        except (BrokenPipeError, OSError):
            pass


def run(
    command: list[str],
    working_directory: Path,
    log_path: Path,
    timeout_seconds: float,
    grace_seconds: float,
    *,
    environment: Mapping[str, str] | None = None,
    on_started: Callable[[int, int, dt.datetime], None] | None = None,
    cancel_requested: Callable[[], bool] | None = None,
    on_completed: Callable[[int, list[str]], None] | None = None,
    force_kill_requested: Callable[[], bool] | None = None,
    on_forced_exit: Callable[[int, int, int], None] | None = None,
) -> int:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    interrupted = threading.Event()

    def interrupt(_signal_number, _frame) -> None:
        interrupted.set()

    previous_handlers = {
        requested: signal.signal(requested, interrupt)
        for requested in (signal.SIGINT, signal.SIGTERM)
    }
    try:
        with log_path.open("wb") as log:
            try:
                # The child may publish evidence before Popen returns or its ownership journal is written.
                started_at = dt.datetime.now(dt.timezone.utc)
                process = subprocess.Popen(
                    command,
                    cwd=working_directory,
                    stdin=None,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    start_new_session=True,
                    env=environment,
                )
            except (OSError, ValueError) as error:
                message = f"Unable to start isolated process: {error}\n".encode()
                log.write(message)
                log.flush()
                sys.stderr.buffer.write(message)
                sys.stderr.buffer.flush()
                if on_completed is not None:
                    on_completed(START_FAILURE_EXIT, [])
                return START_FAILURE_EXIT

            assert process.stdout is not None
            process_group = os.getpgid(process.pid)
            try:
                if on_started is not None:
                    on_started(process.pid, process_group, started_at)
            except Exception as error:
                teardown_errors = _retire_group(process_group, grace_seconds)
                process.wait(timeout=max(1.0, grace_seconds))
                message = f"Process ownership journal failed: {error}\n".encode()
                log.write(message)
                log.flush()
                if on_completed is not None:
                    on_completed(START_FAILURE_EXIT, teardown_errors)
                return START_FAILURE_EXIT
            pump = threading.Thread(target=_pump, args=(process.stdout, log), daemon=True)
            pump.start()
            deadline = time.monotonic() + timeout_seconds
            outcome: int | None = None
            forced = False
            control_error: Exception | None = None
            teardown_errors: list[str] = []
            try:
                while outcome is None:
                    return_code = process.poll()
                    if return_code is not None:
                        outcome = return_code if return_code >= 0 else 128 - return_code
                        break
                    if interrupted.is_set():
                        outcome = INTERRUPTED_EXIT
                        break
                    if cancel_requested is not None and cancel_requested():
                        outcome = INTERRUPTED_EXIT
                        break
                    if time.monotonic() >= deadline:
                        outcome = TIMEOUT_EXIT
                        break
                    if not forced and force_kill_requested is not None:
                        try:
                            if force_kill_requested():
                                error = _signal_group(process_group, signal.SIGKILL)
                                if error:
                                    raise RuntimeError(error)
                                forced = True
                        except Exception as error:
                            control_error = error
                            outcome = START_FAILURE_EXIT
                            break
                    time.sleep(0.05)
            finally:
                teardown_errors.extend(_retire_group(process_group, grace_seconds))
                try:
                    process.wait(timeout=max(1.0, grace_seconds))
                except subprocess.TimeoutExpired:
                    error = _signal_group(process_group, signal.SIGKILL)
                    if error:
                        teardown_errors.append(error)
                    try:
                        process.wait(timeout=max(1.0, grace_seconds))
                    except subprocess.TimeoutExpired as error:
                        teardown_errors.append(f"Owned process group did not exit after SIGKILL: {error}")
                pump.join(timeout=max(1.0, grace_seconds))
                if pump.is_alive():
                    process.stdout.close()
                    pump.join(timeout=1.0)
                if log.tell() == 0:
                    log.write(b"Owned process produced no stdout or stderr output.\n")
                log.flush()
                os.fsync(log.fileno())
            try:
                if forced and on_forced_exit is not None:
                    # Actual Popen return code, not the normalized/cancellation outcome.
                    on_forced_exit(process.pid, process_group, process.returncode)
            finally:
                if on_completed is not None:
                    on_completed(outcome, teardown_errors)
            if control_error is not None:
                raise control_error
            return TEARDOWN_FAILURE_EXIT if teardown_errors else outcome
    finally:
        for requested, previous in previous_handlers.items():
            signal.signal(requested, previous)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout-seconds", type=float, required=True)
    parser.add_argument("--grace-seconds", type=float, default=5.0)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--cwd", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        print("A process command is required.", file=sys.stderr)
        return START_FAILURE_EXIT
    if args.timeout_seconds <= 0 or args.grace_seconds < 0:
        print("Timeout must be positive and grace must be non-negative.", file=sys.stderr)
        return START_FAILURE_EXIT
    if not args.cwd.is_dir():
        print(f"Working directory does not exist: {args.cwd}", file=sys.stderr)
        return START_FAILURE_EXIT
    return run(
        command,
        args.cwd,
        args.log,
        args.timeout_seconds,
        args.grace_seconds,
    )


if __name__ == "__main__":
    raise SystemExit(main())
