// @ts-check
// Sidebar for the documentation portal.
//
// docs/ is a flat folder, so the grouping lives here. A new page under docs/
// must be added to a group below or listed as unpublished in docs-scope.js; the
// check at the bottom of this file fails the build otherwise. ADRs are picked up
// from docs/adr automatically.

const fs = require('fs');
const path = require('path');
const { docsDir, unpublishedFiles } = require('./docs-scope');

const adrIds = fs
  .readdirSync(path.join(docsDir, 'adr'))
  .filter((file) => file.endsWith('.md') && file !== 'README.md')
  .sort()
  .map((file) => `adr/${file.slice(0, -'.md'.length)}`);

/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  docs: [
    { type: 'doc', id: 'README', label: 'Introduction' },
    {
      type: 'category',
      label: 'Get started',
      collapsed: false,
      items: [
        { type: 'doc', id: 'getting-started', label: 'Getting started' },
        { type: 'doc', id: 'features', label: 'Features' },
        { type: 'doc', id: 'architecture', label: 'Architecture' },
        { type: 'doc', id: 'onboarding', label: 'Onboarding a customer project' },
      ],
    },
    {
      type: 'category',
      label: 'How it works',
      items: [
        { type: 'doc', id: 'message-flows', label: 'Message flows' },
        { type: 'doc', id: 'routing-keys', label: 'Routing keys' },
        { type: 'doc', id: 'error-handling', label: 'Error handling' },
        { type: 'doc', id: 'deferred-messages', label: 'Deferred messages' },
        { type: 'doc', id: 'inbox-pattern', label: 'Consumer inbox' },
        { type: 'doc', id: 'pending-handoff', label: 'Pending handoff' },
        { type: 'doc', id: 'orchestration', label: 'Orchestration' },
        { type: 'doc', id: 'circuit-breaker', label: 'Circuit breaker' },
        { type: 'doc', id: 'heartbeat', label: 'Platform heartbeat' },
      ],
    },
    {
      type: 'category',
      label: 'Build integrations',
      items: [
        { type: 'doc', id: 'building-adapters', label: 'Building adapters' },
        { type: 'doc', id: 'azure-functions-hosting', label: 'Azure Functions hosting' },
        { type: 'doc', id: 'pipeline-middleware', label: 'Pipeline middleware' },
        { type: 'doc', id: 'testing', label: 'Testing' },
        { type: 'doc', id: 'extensions', label: 'Extensions' },
        { type: 'doc', id: 'notifications', label: 'Notification channels' },
        { type: 'doc', id: 'integration-intelligence', label: 'Integration Intelligence' },
        { type: 'doc', id: 'cloudevents', label: 'CloudEvents interoperability' },
        { type: 'doc', id: 'asyncapi-mapping', label: 'AsyncAPI mapping' },
      ],
    },
    {
      type: 'category',
      label: 'Deploy and operate',
      items: [
        { type: 'doc', id: 'azure-requirements', label: 'Azure requirements' },
        { type: 'doc', id: 'deployment', label: 'Deployment' },
        { type: 'doc', id: 'private-networking', label: 'Private networking' },
        { type: 'doc', id: 'authentication', label: 'Authentication and identity' },
        { type: 'doc', id: 'storage-providers', label: 'Storage providers' },
        { type: 'doc', id: 'rate-limiting', label: 'Rate limiting' },
        { type: 'doc', id: 'throughput-tuning', label: 'Throughput tuning' },
        { type: 'doc', id: 'mcp-server', label: 'Operator MCP server' },
        { type: 'doc', id: 'failure-agent', label: 'Failure-handling agent' },
        { type: 'doc', id: 'stale-pending-reconcile', label: 'Reconciling stale Pending rows' },
        { type: 'doc', id: 'service-bus-subscription-admin', label: 'Service Bus subscriptions' },
      ],
    },
    {
      type: 'category',
      label: 'Management WebApp',
      items: [
        { type: 'doc', id: 'webapp-monitor', label: 'Monitor wall' },
        { type: 'doc', id: 'webapp-failed-messages', label: 'Failed messages' },
        { type: 'doc', id: 'webapp-insights', label: 'Failed message insights' },
        { type: 'doc', id: 'webapp-simulate', label: 'Simulating traffic' },
      ],
    },
    {
      type: 'category',
      label: 'Reference',
      items: [
        { type: 'doc', id: 'cli', label: 'CLI (nb)' },
        { type: 'doc', id: 'sdk-api-reference', label: 'SDK API' },
        { type: 'doc', id: 'webapp-rest-api', label: 'WebApp REST API' },
      ],
    },
    {
      type: 'category',
      label: 'Architecture decisions',
      link: { type: 'doc', id: 'adr/README' },
      items: adrIds,
    },
    {
      type: 'category',
      label: 'Project',
      items: [
        { type: 'doc', id: 'versioning', label: 'Versioning and releases' },
        { type: 'doc', id: 'contribution-areas', label: 'Where to contribute' },
        { type: 'doc', id: 'dependency-security', label: 'Dependency security' },
      ],
    },
  ],
};

/** Collects every doc id a sidebar item tree refers to. */
function collectIds(items, ids = new Set()) {
  for (const item of items) {
    if (typeof item === 'string') {
      ids.add(item);
    } else if (item.type === 'doc') {
      ids.add(item.id);
    } else if (item.type === 'category') {
      if (item.link?.type === 'doc') {
        ids.add(item.link.id);
      }
      collectIds(item.items, ids);
    }
  }
  return ids;
}

const listed = collectIds(sidebars.docs);
const unlisted = fs
  .readdirSync(docsDir)
  .filter((file) => file.endsWith('.md') && !unpublishedFiles.includes(file))
  .map((file) => file.slice(0, -'.md'.length))
  .filter((id) => !listed.has(id));

if (unlisted.length > 0) {
  throw new Error(
    `These pages under docs/ are not in the portal sidebar: ${unlisted.join(', ')}. ` +
      'Add each to a group in website/sidebars.js, or to unpublishedFiles in website/docs-scope.js.',
  );
}

module.exports = sidebars;
