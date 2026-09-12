"""One owned Quartz mouse-down/up pair; the caller waits for detached input evidence."""
from __future__ import annotations

from contextlib import contextmanager
import math
import signal
import threading
from typing import Any, Callable, Iterator


class PointerLeaseError(RuntimeError):
    pass


@contextmanager
def hold_left_button(backend: Any, point_factory: Callable, x: float, y: float) -> Iterator[None]:
    """Release on normal exit, failed acknowledgement, KeyboardInterrupt or SIGTERM.

    The backend's _post consumes its event, including on failure. Allocate both
    halves before emitting anything; never move the calibrated pointer or replay
    a down. SIGKILL/process crashes cannot run Python cleanup.
    """
    if any(isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v) for v in (x, y)):
        raise PointerLeaseError("Native pointer coordinates must be finite numbers.")
    if threading.current_thread() is not threading.main_thread():
        raise PointerLeaseError("Native pointer leases require the owning main thread.")
    if getattr(backend, "_left_button_lease_active", False):
        raise PointerLeaseError("A native left-button lease is already active.")
    backend._left_button_lease_active = True
    down = up = None
    releasing = False

    def terminate(_number, _frame):
        if not releasing:
            raise PointerLeaseError("Native pointer lease interrupted by SIGTERM.")

    try:
        down = backend._app.CGEventCreateMouseEvent(None, 1, point_factory(x, y), 0)
        up = backend._app.CGEventCreateMouseEvent(None, 2, point_factory(x, y), 0)
        if not down or not up:
            raise PointerLeaseError("Quartz could not allocate both halves of a pointer lease.")
        previous = signal.signal(signal.SIGTERM, terminate)
        try:
            event, down = down, None
            try:
                backend._post(event)
                yield
            finally:
                releasing = True
                event, up = up, None
                backend._post(event)
        finally:
            signal.signal(signal.SIGTERM, previous)
    finally:
        if down:
            backend._cf.CFRelease(down)
        if up:
            backend._cf.CFRelease(up)
        backend._left_button_lease_active = False
