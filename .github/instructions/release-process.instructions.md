---
name: Release Process
description: "Use when preparing, versioning, tagging, publishing, or documenting a release for this repository, including GitHub releases, Windows installers, ZIP packages, MSIX packages, and Microsoft Store notes."
---

# Release process

When the user asks to prepare or publish a release:

1. Determine the previous version tag and compare the current changes against that tag.
2. Apply semantic versioning using `x.y.z`:
   - Increase `x` for substantial, breaking, or major product changes.
   - Increase `y` for important backward-compatible features or improvements.
   - Increase `z` for bug fixes, small features, polish, and other low-impact changes.
3. Update the application version in the project configuration before building.
4. Review and add tests for behavior introduced since the previous tag. Run the complete test suite and do not publish if tests fail.
5. When publishing to GitHub, create and push the matching `vX.Y.Z` tag and ensure the release contains all of these artifacts:
   - Windows x64 installer (`.exe`)
   - Windows x64 portable ZIP
   - MSIX package
6. Use the repository release workflow when it is available, and verify that the workflow completed successfully and that all three artifacts are attached to the GitHub release.
7. Prepare release notes based on the diff from the previous version tag, in both Spanish and English. Include:
   - A detailed list of changes.
   - A concise Microsoft Store description in both languages.
   - Any relevant packaging or signing limitation.
8. In the final response, report the selected version, test result, release URL, artifact links, and the Spanish and English Store notes.

Do not claim that a release is complete until the tag, workflow, release, and all three artifacts have been verified.
