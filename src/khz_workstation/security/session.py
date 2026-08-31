from __future__ import annotations

import os
from collections.abc import Callable


class SessionLockService:
    """Delegates workstation locking to the operating system; does not invent app passwords."""

    def __init__(self, lock_impl: Callable[[], bool] | None = None) -> None:
        self._lock_impl = lock_impl

    @property
    def supported(self) -> bool:
        return self._lock_impl is not None or os.name == "nt"

    def lock_now(self) -> bool:
        if self._lock_impl is not None:
            return bool(self._lock_impl())
        if os.name != "nt":
            return False
        import ctypes
        return bool(ctypes.windll.user32.LockWorkStation())  # type: ignore[attr-defined]
