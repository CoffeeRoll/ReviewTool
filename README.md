# ReviewTool

A local web app for reviewing a git pull request **one commit at a time**. Instead of one big
combined diff, you go through the commits in order, check off files as you go, and leave notes on
specific lines. Your progress is saved, so you can stop and pick the review up again later.

- **Backend:** ASP.NET Core (.NET 10) minimal API that runs your local `git`
- **Frontend:** React 19 + TypeScript (Vite)
- **Storage:** one JSON file per review in `~/.reviewtool/reviews/`

## Features

- **Step through commits.** The sidebar lists the PR's commits oldest first, with a progress ring
  for each one. *Mark reviewed & next* (or <kbd>Shift</kbd>+<kbd>M</kbd>) marks every file in the
  commit as reviewed and moves to the next commit. There is also an **All changes** entry for the
  combined `merge-base..head` diff.
- **Line notes.** Hover a line and click **+** to add a note. Shift-click another line's **+** to
  cover a range of lines. Each note has a type (Comment, Question, Issue, Suggestion, Nit, Praise)
  and can be resolved. You can also add notes on a whole file or a whole commit. The note saves a
  copy of the code it points at, so it still makes sense if the code changes later.
- **Several files at once.**
  - *Stacked* layout: every file in the commit, one after another (like GitHub).
  - *Panes* layout: up to 4 files side by side, each scrolling on its own. Each pane shows either
    the diff or the whole file. A pane can be **pinned** to a commit, so you can compare a file at
    commit 2 with the same file at commit 5.
  - **Browse…** (<kbd>o</kbd>) opens *any* file in the repo at the current commit, even files the
    PR doesn't change, so you can see the surrounding code.
- **Saved and resumable.** Reviewed files, notes, the current commit, open panes and view settings
  are all saved. Reopen a review and you're back where you left off.
- **Handles force pushes.** **Refresh** re-reads the branches. Unchanged commits keep their
  reviewed state. Commits that were rewritten but have notes are kept and marked *outdated*, so no
  notes are lost.
- **Pull requests by number.** Enter a PR number and ReviewTool fetches its head
  (`pull/N/head` for GitHub/Gitea, `merge-requests/N/head` for GitLab) with your normal git
  credentials.
- **Export.** *Finish* lets you set a status (Approved / Changes requested / …) and an overall
  summary, and exports all notes as Markdown to paste into the PR.
- Unified or split diffs, syntax highlighting, expand to the full file, line wrapping,
  a dark theme by default (the theme button switches to light or to your OS setting), and keyboard
  shortcuts (press <kbd>?</kbd>).

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 20+, and `git` on your `PATH`.

```bash
dotnet run --project src/ReviewTool.Server
```

Then open <http://localhost:5178>. The first build runs `npm install` and `npm run build` for the UI
for you.

Enter the path to a local repository, pick a base branch (e.g. `main`) and a head branch (the PR
branch) or a PR number, and click **Start review**.

To try it on sample data:

```bash
scripts/create-demo-repo.sh /tmp/reviewtool-demo   # then review main ← feature/math
```

### Keyboard shortcuts

| Key | Action |
| --- | --- |
| <kbd>n</kbd> / <kbd>p</kbd> (or <kbd>]</kbd> / <kbd>[</kbd>) | Next / previous commit |
| <kbd>j</kbd> / <kbd>k</kbd> | Next / previous file |
| <kbd>v</kbd> | Toggle the current file as reviewed |
| <kbd>Shift</kbd>+<kbd>M</kbd> | Mark the commit reviewed and go to the next one |
| <kbd>a</kbd> | Combined "all changes" diff |
| <kbd>u</kbd> / <kbd>l</kbd> / <kbd>w</kbd> | Toggle unified/split, stacked/panes, line wrap |
| <kbd>o</kbd> | Open any file from the repo |
| <kbd>c</kbd> | Note on the current commit |
| <kbd>Shift</kbd>+<kbd>N</kbd> | Notes panel |
| <kbd>Ctrl</kbd>+<kbd>Enter</kbd> / <kbd>Esc</kbd> | Save / cancel a note |

## Development

Run the API and the Vite dev server (with hot reload) in two terminals:

```bash
dotnet watch --project src/ReviewTool.Server       # API on http://localhost:5178
cd src/ReviewTool.Web && npm install && npm run dev  # UI on http://localhost:5173 (proxies /api)
```

The UI is built into `src/ReviewTool.Server/wwwroot` (git-ignored). `dotnet build` only builds it
when it is missing, so after changing UI code run `npm run build` again, or use the dev server.
To skip the UI build completely, pass `-p:BuildWebUi=false`. `dotnet publish` always rebuilds the UI
and includes it in the output:

```bash
dotnet publish src/ReviewTool.Server -c Release -o out
```

Tests:

```bash
dotnet test                                  # parser, git, service and API tests (needs git)
cd src/ReviewTool.Web && npm test            # frontend unit tests (vitest)
```

### Configuration

Set these in `src/ReviewTool.Server/appsettings.json` or as environment variables
(e.g. `ReviewTool__DataDirectory=/some/dir`):

| Setting | Default | |
| --- | --- | --- |
| `Urls` | `http://localhost:5178` | Address to listen on. Keep it on localhost: the API can read any repository on the machine. |
| `ReviewTool:DataDirectory` | `~/.reviewtool` | Where reviews are stored |
| `ReviewTool:GitExecutable` | `git` | Path to git |
| `ReviewTool:GitTimeoutSeconds` | `120` | Timeout per git command |

## Project layout

```
src/
  ReviewTool.Core/      Git access (GitRunner, GitRepository, DiffParser) and review logic
                        (Review model, JsonFileReviewStore, ReviewService, Markdown export)
  ReviewTool.Server/    ASP.NET Core host; ReviewToolApi.cs maps the HTTP endpoints
  ReviewTool.Web/       React frontend (Vite + TypeScript)
tests/ReviewTool.Tests/ xUnit tests (run against real temporary git repos)
scripts/                create-demo-repo.sh
```

### Embedding in another app

The API is packaged as extension methods, so another ASP.NET Core app can host it:

```csharp
builder.Services.AddReviewTool(builder.Configuration);
// ...
app.UseReviewToolErrors();            // optional: maps errors to ProblemDetails responses
app.MapReviewToolApi("/review-api");  // any prefix
```

The frontend reads its API base URL from `VITE_API_BASE` (default `api`), so it can be built to
point at a different prefix. Reviews are stored through `IReviewStore`, so you can register your own
implementation (e.g. a database) in place of the JSON file store.

### API overview

| Method & path | |
| --- | --- |
| `GET /api/repo?path=` · `GET /api/repo/refs?path=` · `GET /api/repo/commits?path=&base=&head=` | Inspect a repository |
| `GET/POST /api/reviews` | List / create reviews (`{ repoPath, baseRef, headRef, name?, pullRequest? }`) |
| `GET/PATCH/DELETE /api/reviews/{id}` | Read / update (name, summary, status, uiState) / delete |
| `POST /api/reviews/{id}/refresh` | Re-read branches (re-fetches the PR) |
| `GET /api/reviews/{id}/commits/{sha\|range}/files` | Changed files |
| `GET /api/reviews/{id}/commits/{sha\|range}/diff?path=&oldPath=&context=` | One file's diff (`context=-1` for the whole file) |
| `GET /api/reviews/{id}/commits/{sha\|range}/tree` · `.../blob?path=&side=` | Browse files |
| `PUT /api/reviews/{id}/commits/{sha\|range}/reviewed` | `{ paths: [], reviewed: bool }` |
| `POST /api/reviews/{id}/notes` · `PATCH/DELETE .../notes/{noteId}` | Notes |
| `GET /api/reviews/{id}/export.md` | Markdown export |

## Ideas for later

- Post notes as review comments on GitHub/Azure DevOps through their APIs
- "Changes since I last looked" view after a force push (range-diff)
- Desktop wrapper (e.g. Photino or Electron) around the same web UI
