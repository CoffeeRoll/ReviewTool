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
- **Handles force pushes and rebases.** **Refresh** re-reads the branches and compares the new commits
  with the ones you already reviewed (using `git patch-id`):
  - Commits with the same changes (e.g. only rebased) keep their reviewed state and notes, and are
    tagged *rebased*.
  - Rewritten commits are tagged *changed* and new ones *new*.
  - A **Since your last review** view shows only what changed between the old head and the new one,
    limited to files the PR touches, so changes that came in from the base branch are left out.
  - Notes move to the new commit when the code they point at can still be found. Otherwise they stay
    on the old commit, which is kept and marked *outdated*, so no notes are lost.
- **Built for going commit by commit.**
  - **↻ Changes again later:** lines added in one commit that a later commit rewrites are marked
    *↻ n* (with n the later commit's number) and dimmed, so you don't pick apart code that gets
    replaced anyway. The marker sits in a gutter left of the line numbers, so it's always in the same
    place; an orange stripe shows the rest of the affected block. This uses `git blame --reverse`.
    - **Click the marker to peek** without leaving your place. The *Commit n* tab shows the part of that
      commit that changes the line. The *Final version* tab shows what the code ends up as at the end
      of the review, or says it was removed.
    - Markers inside a peek can be clicked too, to follow a line through several rewrites.
    - Ctrl+click (or *Open commit n* in the peek) goes there instead.
  - **← Back:** following a link to another commit (markers, *moved* links, note follow-ups, the notes
    panel) remembers where you were. A *Back* button in the commit bar (or <kbd>b</kbd>) returns you
    to that line.
  - **Notes follow the code:** if a later commit changes the lines a note is about, the note shows up
    again in that commit, marked *possibly addressed?*, with Resolve and *View original* buttons.
  - **File history pane:** a pane in *History* mode steps through every commit in the review that
    touches a file (following renames).
  - **Hunk-level progress:** tick *Hunk reviewed* to check off part of a big file; reviewed hunks
    fold away, and the file is marked reviewed once every hunk is.
- **Easier diff reading.**
  - The changed words within a line are highlighted.
  - **Hide whitespace changes** (<kbd>i</kbd>) uses git's `-w` option.
  - **Moved code** is shown in purple with *moved to* / *moved from* links, even across files and
    when re-indented.
  - **Generated files** start collapsed: lockfiles, `*.Designer.cs`, `*.g.cs`, minified files, EF
    migration snapshots, and anything marked `linguist-generated` in `.gitattributes`.
  - The file list can show a **folder tree** or a flat list.
  - Very large diffs render in chunks so the page stays responsive.
  - **Line spacing** (View menu): *Normal* for readability, or *Compact* to fit about a quarter more
    code on screen. Remembered across reviews, like the theme.
- **Post to GitHub.** Send the review (summary, verdict and notes) to the pull request. Notes become
  inline comments wherever their code is still in the final diff (see *Posting to GitHub*).
- **Pull requests by number.** Enter a PR number and ReviewTool fetches its head
  (`pull/N/head` for GitHub/Gitea, `merge-requests/N/head` for GitLab) with your normal git
  credentials.
- **Export.** *Finish* lets you set a status (Approved / Changes requested / …) and an overall
  summary, and exports all notes as Markdown to paste into the PR.
- Unified or split diffs, syntax highlighting, expand to the full file, line wrapping,
  a dark theme by default (the theme button switches to light or to your OS setting), and keyboard
  shortcuts (press <kbd>?</kbd>).

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 20+ (to build the UI), and `git` on your `PATH`.

### Install the `reviewtool` command

```bash
scripts/install-tool.sh          # macOS / Linux
scripts/install-tool.ps1         # Windows (PowerShell)
```

This builds the UI, packs ReviewTool as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools)
into `artifacts/nupkg`, and installs it globally. Then, inside any repository:

```bash
reviewtool                       # review the current branch against the default branch
reviewtool --pr 123              # review pull request #123 (fetched from origin)
reviewtool ../api --base develop # another repo, another base branch
reviewtool --help
```

ReviewTool starts on <http://localhost:5178>, opens your browser on the review, and **resumes** it if you've
already started reviewing the same branches. If it's already running, the command reuses that server. If
there's nothing to review (e.g. you're on `main`), the home page opens with the repository filled in.

### Run from source

```bash
dotnet run --project src/ReviewTool.Server
```

Then open <http://localhost:5178>. The first build runs `npm install` and `npm run build` for the UI
for you. Enter the path to a local repository, pick a base branch (e.g. `main`) and a head branch (the PR
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
| <kbd>i</kbd> | Hide / show whitespace-only changes |
| <kbd>d</kbd> | Dim lines that a later commit rewrites |
| <kbd>s</kbd> | Changes since your last review |
| <kbd>b</kbd> | Back to where you were before following a link |
| <kbd>o</kbd> | Open any file from the repo |
| <kbd>c</kbd> | Note on the current commit |
| <kbd>Shift</kbd>+<kbd>N</kbd> | Notes panel |
| <kbd>Ctrl</kbd>+<kbd>Enter</kbd> / <kbd>Esc</kbd> | Save / cancel a note |

## Posting to GitHub

**Finish → Post to GitHub** posts the review to the pull request as a single GitHub review: your summary,
a verdict (*Comment*, *Approve* or *Request changes*), and your notes.

- **Inline where possible.** GitHub only accepts inline comments on the pull request's overall diff at
  its latest commit, but your notes were written against individual commits. ReviewTool traces each
  note's lines forward to the head commit (`git blame --reverse`). If the code is unchanged and inside
  the PR's diff, the note becomes an inline comment on its new line number.
- **Everything else goes in the review text,** grouped by commit and with a code snippet. That covers
  code rewritten by a later commit, commit- and file-level notes, and lines outside the diff. The
  dialog shows which notes go where, and why, before anything is sent.
- **Safe to repeat.** Posted notes are recorded in the review, so posting again only sends new notes.
  ReviewTool refuses to post if the PR on GitHub is at a different commit than the one you reviewed,
  because comments would land on the wrong lines. Push or refresh first.
- **Finding the PR.** The PR is found from the repository's GitHub remote and your branch name, or from
  the number you started the review with. You can also type the number in the dialog.

**Signing in.** ReviewTool looks for a token in this order and never stores it in reviews or sends it to the browser:
1. `ReviewTool:Credentials:Tokens:github` in configuration
2. `GITHUB_TOKEN` / `GH_TOKEN` (or `GH_ENTERPRISE_TOKEN` for GitHub Enterprise)
3. the GitHub CLI: `gh auth token` (so `gh auth login` is all most people need)
4. git's own credential helper (the one you push with)

The token needs permission to write pull request reviews.

**GitHub Enterprise Server** works automatically for hosts named `github.*`. For other hosts, add them to
`ReviewTool:GitHub:EnterpriseHosts`, or map them to an API URL with `ReviewTool:GitHub:ApiBaseUrls`.

### Adding another platform (GitLab, Azure DevOps, …)

Publishing is split so a new platform only has to talk to its API:

| Piece | Platform-neutral? | Where |
| --- | --- | --- |
| Find remotes, credentials lookup, preview, "already posted" bookkeeping | yes | `Publishing/PublishingService.cs`, `CredentialResolver.cs` |
| Decide which notes are inline and map their lines to the head commit | yes | `Publishing/PublishPlanner.cs` |
| Markdown for comments and the review text | yes | `Publishing/PublishFormatter.cs` |
| Talk to the platform's API | **no** | `Publishing/GitHub/GitHubPublisher.cs` |

To add a platform, implement `IReviewPublisher`:
- `CanHandle(remote)`: recognise the host
- `GetCredentialHints`: environment variables, CLI tool, help text
- `GetPullRequestAsync` / `FindPullRequestAsync`
- `PublishAsync`: turn the `PublishPlan` into API calls

Then register it in `AddReviewTool()` (`ReviewToolApi.cs`). The UI picks up any platform the server
reports. `PublicationRecord` already stores the provider id, so the bookkeeping works for every platform.

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
| `ReviewTool:DataDirectory` | `~/.reviewtool` | Where reviews are stored (`--data-dir`) |
| `ReviewTool:GitExecutable` | `git` | Path to git |
| `ReviewTool:GitTimeoutSeconds` | `120` | Timeout per git command |
| `ReviewTool:AllowedHosts` | `[]` | Host names the API answers to besides localhost (`--allowed-host`). See *Security*. |
| `ReviewTool:Credentials:Tokens` | `{}` | Tokens keyed by `github`, a host, or `github:host` |
| `ReviewTool:Credentials:UseCliTools` / `UseGitCredentialHelper` | `true` | Whether to ask `gh` / git's credential helper for a token |
| `ReviewTool:GitHub:EnterpriseHosts` / `ApiBaseUrls` | empty | GitHub Enterprise hosts, and their API URLs if not `https://host/api/v3` |

The address defaults to `http://localhost:5178`. Change it with `--port` / `--host`, or with `--urls`.
Keep it on localhost: the API can read any repository on the machine. Installed as a tool, the settings
file is next to the tool; environment variables are usually easier.

### Security

The API can read any git repository on the machine and post to GitHub with your token, so:

- **It listens on localhost only** by default.
- **It answers only requests addressed to known host names.** This blocks *DNS rebinding*, where a
  web page points its own domain at `127.0.0.1` to get around the browser's same-origin rules.
  - `localhost`, `127.0.0.1` and `[::1]` always work.
  - Add other names with `ReviewTool:AllowedHosts` or `--allowed-host`. Patterns work like ASP.NET's
    `AllowedHosts`: `reviews.example.com`, `*.corp.example`, or `*` to turn the check off.
  - `--host some-name` allows that name automatically.
  - With `--host 0.0.0.0`, list each name or IP the machine is reached by.
- **Embedded in another app** (see below), the check travels with `MapReviewToolApi()`.
  - If that app already sets ASP.NET's `AllowedHosts` (anything but `*`), ReviewTool uses the same
    list, so nothing extra is needed.
  - Otherwise set `ReviewTool:AllowedHosts` to the app's public host name(s).
  - Behind a reverse proxy that rewrites the host, run `UseForwardedHeaders()` first so the check sees
    the public name. Or use `*` if the proxy already validates hosts.
- Cross-site requests from other pages are already blocked: the API only accepts JSON, and has no
  CORS policy outside development.

## Project layout

```
src/
  ReviewTool.Core/      Git access (GitRunner, GitRepository, DiffParser), review logic
                        (Review model, JsonFileReviewStore, ReviewService, Markdown export)
                        and Publishing/ (platform-neutral planner + GitHub adapter)
  ReviewTool.Server/    ASP.NET Core host and the `reviewtool` command (Cli/);
                        ReviewToolApi.cs maps the HTTP endpoints
  ReviewTool.Web/       React frontend (Vite + TypeScript)
tests/ReviewTool.Tests/ xUnit tests (real temporary git repos; GitHub API is faked)
scripts/                create-demo-repo.sh, install-tool.sh / .ps1
artifacts/nupkg/        packed tool (git-ignored)
```

### Embedding in another app

The API is packaged as extension methods, so another ASP.NET Core app can host it:

```csharp
builder.Services.AddReviewTool(builder.Configuration);
// ...
app.UseReviewToolErrors();            // optional: maps errors to ProblemDetails responses
app.MapReviewToolApi("/review-api");  // any prefix
```

The API only answers requests for host names it knows. If your app is served under its own name, it
either inherits your app's `AllowedHosts` or needs `ReviewTool:AllowedHosts` (see *Security*):

```csharp
builder.Services.Configure<ReviewToolOptions>(o => o.AllowedHosts.Add("portal.example.com"));
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
| `GET /api/reviews/{id}/commits/{sha\|range\|since}/files?ignoreWhitespace=` | Changed files (`range` = combined diff, `since` = since your last review) |
| `GET /api/reviews/{id}/commits/{sha\|range\|since}/diff?path=&oldPath=&context=&ignoreWhitespace=` | One file's diff (`context=-1` for the whole file) |
| `GET /api/reviews/{id}/commits/{sha\|range}/tree` · `.../blob?path=&side=` | Browse files |
| `PUT /api/reviews/{id}/commits/{sha\|range}/reviewed` | `{ paths: [], reviewed: bool }` |
| `PUT /api/reviews/{id}/commits/{sha\|range}/reviewed-lines` | `{ path, ranges: ["N12-20", "O5"], reviewed }` (hunk progress) |
| `GET /api/reviews/{id}/commits/{sha\|range}/moved` | Moved-code blocks |
| `GET /api/reviews/{id}/commits/{sha}/later-changes?path=` | Lines a later commit rewrites |
| `GET /api/reviews/{id}/commits/{sha}/final-location?path=&line=&endLine=` | Where lines ended up in the final version |
| `GET /api/reviews/{id}/followups` · `GET /api/reviews/{id}/file-history?path=` | Notes to revisit · a file's commits |
| `POST /api/reviews/{id}/notes` · `PATCH/DELETE .../notes/{noteId}` | Notes |
| `GET /api/reviews/{id}/export.md` | Markdown export |
| `POST /api/reviews/open` | Resume the matching review or create one (used by the `reviewtool` command) |
| `GET /api/reviews/{id}/publish/targets` | Hosted repositories this review can be posted to, and whether you're signed in |
| `POST /api/reviews/{id}/publish/preview` · `POST /api/reviews/{id}/publish` | `{ pullRequestNumber?, verdict?, includeResolved?, republish? }`: plan, then post |

## Ideas for later

- GitLab and Azure DevOps publishers (see *Adding another platform*)
- Pull existing PR comments into the review
- Desktop wrapper (e.g. Photino or Electron) around the same web UI
