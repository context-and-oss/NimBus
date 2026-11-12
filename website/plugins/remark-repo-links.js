// Remark plugin: point links that leave the published docs at GitHub.
//
// The Markdown in docs/ links to source files, samples, workflows, plans and
// specs with relative paths, which is what makes those links work when the file
// is read on GitHub. None of those targets exist on the portal, so this plugin
// rewrites them to absolute GitHub URLs at build time and leaves the Markdown
// source untouched.

const fs = require('fs');
const path = require('path');

const hasScheme = /^[a-z][a-z0-9+.-]*:/i;

function walk(node, visit) {
  visit(node);
  if (Array.isArray(node.children)) {
    for (const child of node.children) {
      walk(child, visit);
    }
  }
}

/**
 * @param {object} options
 * @param {string} options.repoRoot Absolute path of the repository root.
 * @param {string} options.repoUrl Repository URL, e.g. https://github.com/org/repo.
 * @param {string} options.branch Branch the rewritten links point at.
 * @param {(absolutePath: string) => boolean} options.isServedByPortal
 *   True for targets the docs plugin resolves itself.
 */
module.exports = function remarkRepoLinks({ repoRoot, repoUrl, branch, isServedByPortal }) {
  return (tree, file) => {
    const sourceDir = path.dirname(file.path);

    walk(tree, (node) => {
      if (node.type !== 'link' && node.type !== 'definition') {
        return;
      }

      const url = node.url;
      if (!url || hasScheme.test(url) || url.startsWith('#') || url.startsWith('/')) {
        return;
      }

      const suffixStart = url.search(/[?#]/);
      const targetPath = suffixStart === -1 ? url : url.slice(0, suffixStart);
      const suffix = suffixStart === -1 ? '' : url.slice(suffixStart);
      if (!targetPath) {
        return;
      }

      const target = path.resolve(sourceDir, decodeURIComponent(targetPath));
      if (isServedByPortal(target)) {
        return;
      }

      const repoRelative = path.relative(repoRoot, target);
      if (repoRelative.startsWith('..') || path.isAbsolute(repoRelative)) {
        return;
      }

      if (!fs.existsSync(target)) {
        const source = path.relative(repoRoot, file.path);
        throw new Error(`${source} links to "${url}", which does not exist in the repository.`);
      }

      const kind = fs.statSync(target).isDirectory() ? 'tree' : 'blob';
      const repoPath = repoRelative.split(path.sep).map(encodeURIComponent).join('/');
      node.url = `${repoUrl}/${kind}/${branch}/${repoPath}${suffix}`;
    });
  };
};
