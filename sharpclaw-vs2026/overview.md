# SharpClaw for Visual Studio 2026

SharpClaw is an AI agent integration for Visual Studio 2026. It connects your IDE
directly to the SharpClaw backend, enabling AI-driven automation, code assistance,
and agent-based workflows without leaving Visual Studio.

## Features

- Live connection to the SharpClaw agent runtime
- Editor session bridging: send context, receive completions and actions
- Tool-aware agent dispatch from inside Visual Studio
- Configurable via the SharpClaw options page

## Getting Started

1. Install the SharpClaw backend from [github.com/mkn8rn/SharpClaw](https://github.com/mkn8rn/SharpClaw).
2. Start the SharpClaw Core API service (`SharpClaw.Application`).
3. Open Visual Studio 2026 then connect **Tools → SharpClaw → Connect**
4. Configure settings under **Tools → Options → SharpClaw**.
5. Open SharpClaw Chat window with **Tools → SharpClaw → SharpClaw Chat**.

## Source & Issues

[https://github.com/mkn8rn/SharpClaw](https://github.com/mkn8rn/SharpClaw)
