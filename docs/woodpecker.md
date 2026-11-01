# Legacy controller boundary

Woodpecker was the release/validation controller in the original private repository. It is not a continuing controller for this canonical public-source candidate; `.github/workflows/ci.yml` owns the replacement contracts.

Until an explicit handoff is approved, the existing controller may remain the sole temporary production owner. Before arming new GitHub Actions releases, drain its active jobs and disable only Nailbiter's scheduling/webhook/other publishing routes. Preserve shared runner infrastructure and other projects. Do not restore a parallel old Actions deployment or run a second private publishing repository.

After accepted migration, all development, issues/PRs, CI and CD belong to the new public repository. The original stays private and inactive as evidence. Follow [the deployment/cutover runbook](deployment.md); this document is not permission to activate or disable a live controller.
