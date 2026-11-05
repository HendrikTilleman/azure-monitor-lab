# SRE Mission Control: rules for GitHub Copilot
The full specification is DESIGN.md at the repository root. Read the relevant sections before changing anything.

Honesty (never break these)
- Every number, alert, agent message and timestamp on the Mission Control screen comes from a live source: the portal pulse, Azure Monitor alerts, Container insights, Application Insights availability results, the Azure SRE Agent thread or the agent's audit log.
- Never invent, interpolate, hard-code or smooth agent output, root causes or metrics. A missing value is shown as "not measured".
- Every fallback is labelled on screen: "RECORDED RUN · not live", "Agent paged manually", "Paused by runbook · not the agent", and "Simulated data" for the local design harness.
- No typing animations, fake progress bars or spinners that suggest agent activity without data behind it.
- Agent actions are approved only in the Azure SRE Agent portal. Mission Control shows the approval status; it never approves.
- The agent instructions and the knowledge file never contain hints about the injected fault.

Scope and reuse
- This repository is a fork of azure-monitor-lab. Extend it; do not redesign it. Reuse the Control Center web app, the Lab Operations runner, the existing alerts, the AKS workloads and the SRE Agent deployment.
- Keep changes upstream-friendly: follow the lab's conventions, put new files where DESIGN.md section 8 says, avoid unrelated refactors, and never delete or rewrite upstream files beyond the documented edits.
- Mission add-ons are installed on top of a deployed lab by scripts/mission-control.ps1 -Action Setup. Do not change the lab's main Bicep or Terraform deployment unless a work item says so.

Engineering
- One work item per pull request. Small, reviewable changes. Describe what you verified and what the owner must verify in Azure.
- /api/mission/* uses the same operator allowlist, same-origin check and request header as /api/operations. /api/portal/* allows anonymous calls but requires the lab customer key header and has its own rate limit.
- PowerShell 7 scripts are idempotent, safe to run twice, and only touch the lab resource group and the Kubernetes namespace demo. Never delete Azure resources.
- No secrets in the repository. Secrets live in App Service settings and Kubernetes secrets created by the setup script.
- Tests: node --test for the screen's view-model, unit tests for the phase and mark logic, a smoke test for the portal API.
- Keep docs/SRE-MISSION-CONTROL.md up to date with every behaviour change.
