// Which files under ../docs are published on the portal.
//
// Everything in docs/ is published unless it is listed here. Plans, specs and
// working notes stay in the repository: links to them from a published page are
// rewritten to GitHub by plugins/remark-repo-links.js.

const path = require('path');

const docsDir = path.resolve(__dirname, '..', 'docs');
const repoRoot = path.resolve(__dirname, '..');

/** Folders under docs/ that are not published. */
const unpublishedDirs = ['plan', 'spec'];

/** Top-level files under docs/ that are not published. */
const unpublishedFiles = [
  'feature-request-asyncapi.md',
  'feature-request-cloudevents.md',
  'issues-to-create.md',
  'performance-improvements-handover.md',
];

/** Glob patterns for the docs plugin's `exclude` option. */
const excludeGlobs = [...unpublishedDirs.map((dir) => `${dir}/**`), ...unpublishedFiles];

/**
 * True when the absolute path is something the docs plugin serves itself:
 * a published page, or an asset (image, diagram) next to one.
 */
function isServedByPortal(absolutePath) {
  const relative = path.relative(docsDir, absolutePath);
  if (relative.startsWith('..') || path.isAbsolute(relative)) {
    return false;
  }

  const segments = relative.split(path.sep);
  if (unpublishedDirs.includes(segments[0])) {
    return false;
  }

  return !(segments.length === 1 && unpublishedFiles.includes(segments[0]));
}

module.exports = { docsDir, repoRoot, unpublishedDirs, unpublishedFiles, excludeGlobs, isServedByPortal };
