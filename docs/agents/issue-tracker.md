# Issue tracker: GitHub

The canonical target for issues and specs is `scowalt/nailbiter`.
During migration, verify the target exists and GitHub writes are explicitly authorized before using the commands below. Existing private issue/PR numbers are not automatically numbers in the new repository.
Use the `gh` CLI from the canonical clone; elsewhere, pass
`--repo scowalt/nailbiter`.

## Conventions

- Create: `gh issue create --title "..." --body-file <file>`.
  Use a Markdown file or heredoc for multiline bodies.
- Read: `gh issue view <number> --json title,body,state,labels,comments`.
- List: `gh issue list --state open --json number,title,body,labels,comments`.
  Use appropriate label/state filters and pagination for exhaustive queries.
- Comment: `gh issue comment <number> --body "..."`.
- Label: `gh issue edit <number> --add-label "..." --remove-label "..."`.
- Close: `gh issue close <number> --comment "..."`.

When a skill says "publish to the issue tracker", create a GitHub issue.
When it says "fetch the relevant ticket", read the issue and its comments.

## Pull requests as a triage surface

**PRs as a request surface: no.**

GitHub shares numbering between issues and PRs. For an ambiguous reference,
try `gh pr view <number>` and fall back to `gh issue view <number>`.

## Wayfinding operations

- Map: one issue labelled `wayfinder:map`, holding Notes,
  Decisions-so-far, and Fog.
- Child ticket: link it as a native GitHub sub-issue. If unavailable,
  add it to the map's task list and put `Part of #<map>` in its body.
  Use `wayfinder:<type>` labels: research, prototype, grilling, or task.
- Blocking: use native issue dependencies via
  `gh api --method POST repos/scowalt/nailbiter/issues/<child>/dependencies/blocked_by -F issue_id=<blocker-db-id>`.
  Retrieve the database ID with
  `gh api repos/scowalt/nailbiter/issues/<blocker> --jq .id`.
  If unavailable, record `Blocked by: #<number>` in the child body.
- Frontier: in map order, select the first open child with no assignee
  and no open blockers.
- Claim: `gh issue edit <number> --add-assignee @me`.
- Resolve: comment with the answer, close the ticket, and append a
  summary and link to the map's Decisions-so-far.
