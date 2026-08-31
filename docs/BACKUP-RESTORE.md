# Backup and Restore

## Backup format

KHZ backup is a ZIP archive containing workspace files plus `KHZ-BACKUP-MANIFEST.json`.

The manifest records:

- format identifier `KHZ-WORKSPACE-BACKUP-V1`;
- workspace ID;
- UTC creation time;
- SHA-256 hash of every archived file.

## Publication

Backup writes to a unique temporary sibling. The temporary archive is reopened, its manifest is parsed, workspace identity can be checked, every member hash is recomputed, and only then is the file published with `os.replace`.

A partial archive is not reported as success.

## Restore

Restore:

1. validates ZIP structure and manifest;
2. rejects absolute and `..` paths;
3. extracts into a staging directory;
4. recomputes every file hash;
5. preserves an existing destination by renaming it;
6. atomically renames the staged directory into place where filesystem semantics allow;
7. restores the preserved original if publication fails after preservation.

## Tests

Regression tests cover successful backup/restore, backup publication failure, and corrupt restore input. `NO_AI_BASELINE` also performs backup and restore.
