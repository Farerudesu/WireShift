# Contributing to WireShift

Welcome, and thank you for your interest in contributing to **WireShift**! 🎉

WireShift is an open source Windows per-app network interface binding tool, and every contribution — whether it's a bug report, a feature idea, a documentation fix, or a code change — is genuinely valued. This project thrives because of people like you.

---

## How to Contribute

### Reporting Bugs

If you've found a bug, please [open a GitHub Issue](../../issues) and include the following:

- **OS version** (e.g., Windows 11 23H2)
- **Steps to reproduce** the problem
- **Expected behavior** vs. **actual behavior**
- **Service console output** (copy/paste or screenshot from the NetBinder.Service terminal)

The more detail you provide, the faster we can track it down.

### Suggesting Features

Have an idea that would make WireShift better? We'd love to hear it.

- Open a [GitHub Issue](../../issues) with the **`[Feature Request]`** tag in the title
- Describe the **use case** — what problem does this solve?
- Outline a **proposed solution** if you have one in mind

### Code Contributions

1. **Fork** the repository
2. **Create a feature branch**
   ```bash
   git checkout -b feature/amazing-feature
   ```
3. **Make your changes**
4. **Test thoroughly** — build the solution, run both the service and UI, and verify that bindings work correctly
5. **Commit with descriptive messages**
   ```bash
   git commit -m "Add weighted round-robin to LoadBalancerService"
   ```
6. **Push** your branch and **open a Pull Request**

---

## Development Setup

### Prerequisites

| Requirement | Details |
|---|---|
| **OS** | Windows 10/11 64-bit |
| **SDK** | .NET 8.0 SDK |
| **IDE** | Visual Studio 2022 or VS Code with the C# extension |
| **Privileges** | Administrator — required for WinDivert, WFP, and named pipe testing |

### Project Structure

```
src/
├── NetBinder.Shared/     # Shared models and protocol
├── NetBinder.Service/    # Background service (WinDivert, WFP, proxies)
└── NetBinder.UI/         # WPF desktop application
```

### Running Locally

You'll need **two terminals**, both running as Administrator:

```bash
# Terminal 1 — Start the background service
cd src/NetBinder.Service
dotnet run

# Terminal 2 — Start the desktop UI
cd src/NetBinder.UI
dotnet run
```

### Key Architecture Notes

- **Service ↔ UI communication** uses a Named Pipe (`NetBinderService`)
- **Protocol** — 4-byte length-prefixed JSON messages
- **WinDivert** handles transparent packet redirection at the network layer
- **WFP filters** manage per-app permit/block rules
- **SOCKS5 proxy** provides fallback routing when transparent mode isn't viable

---

## 🚀 High-Priority Contribution: Load Balancing

> [!IMPORTANT]
> This is the most impactful feature area open for contribution right now. The data model groundwork is already in place — what's needed is the core logic and UI integration.

### What's Already Built

The foundational types are defined and ready to use:

| Component | Location | Status |
|---|---|---|
| `BindingMode` enum | `NetBinder.Shared` | ✅ Has `SingleBind` and `LoadBalance` values |
| `InterfaceWeight` class | `NetBinder.Shared` | ✅ Has `InterfaceIndex`, `InterfaceName`, `Weight` |
| `BindingMapping.Mode` | `NetBinder.Shared` | ✅ Supports load-balance mode selection |
| `BindingMapping.TargetInterfaces` | `NetBinder.Shared` | ✅ Holds the weighted interface list |
| `LoadBalancerService.cs` | `NetBinder.Service` | 🔲 Stub method signatures only |

### What Needs to Be Implemented

1. **`LoadBalancerService`** — Weighted round-robin connection distribution algorithm
2. **Session Affinity** — LRU cache mapping destination IP → interface index so existing connections stay on the same path
3. **Interface Health Monitoring** — Periodic TCP probes to detect interface failures
4. **Automatic Failover** — When an interface goes down, redistribute its connections across healthy interfaces
5. **UI: Load Balancing Tab** — Enable the currently disabled "Load Balancing" tab with:
   - Interface selection with weight sliders
   - Per-interface throughput statistics
   - Health status indicators (up / degraded / down)
6. **`RedirectorService` Integration** — Modify `MatchBinding` to support load-balanced bindings alongside single-bind

### Getting Started with Load Balancing

- Read **`NOTES.md`** for architectural decisions and design rationale
- Start with the stub implementation at **`src/NetBinder.Service/Services/LoadBalancerService.cs`**
- Each sub-task above can be tackled independently — pick one and open a PR!

> [!TIP]
> If you're new to the codebase, implementing **Session Affinity** (task 2) is a great self-contained starting point. It requires minimal knowledge of the packet redirection layer and has clearly defined inputs and outputs.

---

## Code Style

- Follow standard **C# conventions** (PascalCase for public members, camelCase for locals)
- Use **meaningful** variable and method names — clarity over brevity
- Add **XML doc comments** (`///`) on all public APIs
- Keep methods **focused** and under **50 lines** where possible
- When in doubt, match the style of the surrounding code

---

## Pull Request Guidelines

- PRs should target the **`main`** branch
- Include a **clear description** of what your changes do and why
- Ensure **`dotnet build`** passes with **0 warnings and 0 errors**
- Test both **Transparent** and **Manual Proxy** routing modes
- Update **`NOTES.md`** if you're making architectural changes or adding new subsystems

---

## Code of Conduct

Be respectful, inclusive, and constructive. We're all here to build something cool together. Treat every contributor — regardless of experience level — with kindness and patience.

---

Thank you for helping make WireShift better! 💙
