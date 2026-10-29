# Releases

Before preparing, tagging, publishing, or resuming **any release**, load and follow
[the prepare-release skill](./skills/prepare-release/SKILL.md). This is mandatory,
including when the request is simply "make a release."

Prepend the new version to the bundled What's New history before releasing; preserve
all older release entries and version headings. Run the existing release-notes
validator once after finalizing it, without extra What's New UI smoke checks.
Do not tag or publish until the latest version matches the app and release tag
and its instructions match the shipped features.
