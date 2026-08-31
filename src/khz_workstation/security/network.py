from __future__ import annotations

import ipaddress
import socket
from dataclasses import dataclass
from urllib.parse import urlparse

from ..models import NetworkMode


class NetworkDenied(PermissionError):
    pass


@dataclass
class NetworkPolicy:
    mode: NetworkMode = NetworkMode.DENY
    allowlist: tuple[str, ...] = ()

    def authorize_host(self, host: str) -> bool:
        if self.mode == NetworkMode.UNRESTRICTED:
            return True
        if self.mode == NetworkMode.DENY:
            return False
        if host.lower() == "localhost":
            return self.mode in (NetworkMode.LOOPBACK_ONLY, NetworkMode.ALLOWLIST)
        try:
            ips = {ipaddress.ip_address(host)}
        except ValueError:
            try:
                ips = {ipaddress.ip_address(row[4][0]) for row in socket.getaddrinfo(host, None)}
            except OSError:
                ips = set()
        if self.mode == NetworkMode.LOOPBACK_ONLY:
            return bool(ips) and all(ip.is_loopback for ip in ips)
        if self.mode == NetworkMode.ALLOWLIST:
            return host.lower() in {x.lower() for x in self.allowlist} or (bool(ips) and all(ip.is_loopback for ip in ips))
        return False

    def authorize_url(self, url: str) -> None:
        parsed = urlparse(url)
        host = parsed.hostname
        if not host or not self.authorize_host(host):
            raise NetworkDenied(f"Network destination denied by policy: {host or url}")
