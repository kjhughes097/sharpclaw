---
name: daily-notes
description: Keep a dated daily notes file in the workspace. Use when the user asks to note, log or journal something, or asks what they noted on a day.
---

# Daily notes

Notes live in one Markdown file per day: `notes/YYYY-MM-DD.md` (for example
`notes/2026-10-30.md`). Each note is one timestamped bullet.

## Adding a note

1. Call `current_time` (with the user's time zone if you know it) to get today's date and the time.
2. Call `workspace_append` on `notes/<date>.md` with one line, ending in a newline (append adds
   nothing of its own):

   ```markdown
   - HH:MM — the note, in the user's words, tidied but not reworded
   ```

   If the file doesn't exist yet, `workspace_append` creates it; start it with a heading line
   `# Notes for <date>` followed by a blank line, then the bullet, in the same call.
3. Reply briefly: confirm what you noted and the file. Don't repeat the whole day.

Several things at once? One bullet each, in one append.

## Finding notes

- "What did I note today / on Tuesday / on 3 March?" Work out the date (call `current_time` for
  "today" and relative days), then `workspace_read` that day's file. No file means nothing was noted.
- "When did I mention X?" Call `workspace_search` with the text and `path: notes`. Report the dates
  and the matching bullets.
- "What did I note this week?" Read each day's file that exists (`workspace_find` with
  `pattern: *.md` and `path: notes` lists them) and summarise by day.

## Rules

- Never rewrite or delete earlier notes unless the user asks; append only.
- Keep the user's meaning. Fix obvious typos, but don't add facts or opinions.
- Times are 24-hour, in the user's time zone when you know it, otherwise UTC (say so once).
