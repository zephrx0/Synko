# Synko repository rules

## GitHub identity

- Owner: `zephrx0`, display name `zephyr`, GitHub ID `134763007`.
- Git author and committer: `zephyr <134763007+zephrx0@users.noreply.github.com>`.
- Verify the authenticated account before publishing. Set repository-local Git identity explicitly and inspect the commits to be pushed. Do not inherit an unchecked global Git email.

## Releases

- Publish an authorized version directly as a stable release: `draft: false`, `prerelease: false`. Do not create draft releases, including temporary drafts for asset uploads.
- Complete local packaging, checksums and release notes before publication.
- Release assets: ZIP, versioned EXE and its SHA-256 file. No preview PNG/JPEG/SVG assets and no screenshots in release descriptions.
- Keep the owner's supplied screenshot in the README.
- Do not modify application binaries or change their checksums merely to update repository metadata or authorship.
- Use relevant GitHub topics such as Apple Music, Discord Rich Presence, YouTube and Windows for discovery.
