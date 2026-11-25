# Wording house rules

Rules for the words we choose, in every house repo and every artifact: code identifiers,
comments, docs, commit messages, catalog nodes and thread notes. Started 2026-11-25.

As with `note.csharp-house-rules`: prefer a check that enforces a rule over a paragraph
here. This one is advisory until something runs it.

## 1. Allow / deny, not white / black

Decided 2026-11-25, during the TodoConnector handover, when Lori noticed the connector's
access list was already called an allow-list: "I note we are using the allow/deny
wording. If that's not in as a rule, it should be :)"

Name a list by what it does, not by a colour:

| Use | Not |
|---|---|
| allow-list, allowlist, allowed (`AllowedUsers`, `IsAllowed`) | whitelist |
| deny-list, denylist, blocked, refused | blacklist |
| primary / replica, main / secondary, leader / follower | master / slave |
| `main` as a default branch | `master` |

Why: the descriptive word says what the list does -- an allow-list allows -- and the
colour metaphor makes the reader translate. The descriptive word is also the one already
in use here (TodoConnector's `AllowedUsers`, RazorGraph's attribute-policy denylist, the
permission allow-lists in `note.janet-machine-move`), so this records the house practice
rather than changing it. Measured 2026-11-25: no whitelist, blacklist, master or slave in
TodoConnector or in JanetHome's notes.

Exception: a name we do not own is quoted as it is -- a third-party API parameter, a CLI
flag, a remote's existing branch name. Say so where it appears, rather than renaming it
and breaking the reference.

Enforcement idea: a pre-commit check over staged text for the "Not" column, honouring an
inline marker for the quoted-third-party exception.
