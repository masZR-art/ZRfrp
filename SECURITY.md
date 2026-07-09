# Security

## Deployment

- Put the Web panel behind HTTPS before exposing it to the Internet.
- Keep the built-in frps Dashboard bound to `127.0.0.1`.
- Restrict ports `7600` and `7000` with the cloud firewall where possible.
- Store the generated administrator password, client API Key, and frp Token separately.
- Do not reuse the frp Token as the panel password or client API Key.

The installer gives the `zrfrp` service account permission to start, stop, and restart only the `zrfrp-frps` systemd unit. The service does not run as root.

## Reporting

Please report vulnerabilities privately to the repository owner rather than opening a public issue with credentials or exploit details.
