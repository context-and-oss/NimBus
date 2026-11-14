// @ts-check
// Documentation portal for NimBus. The content is the Markdown in ../docs; this
// project only adds navigation, search and theming around it.
//
// The portal is published under /NimBus/docs/ on GitHub Pages, next to the
// hand-written landing site in ../site (see .github/workflows/pages.yml).

const { themes: prismThemes } = require('prism-react-renderer');
const { repoRoot, excludeGlobs, isServedByPortal } = require('./docs-scope');
const remarkRepoLinks = require('./plugins/remark-repo-links');

const repoUrl = 'https://github.com/context-and-oss/NimBus';
const websiteUrl = 'https://context-and-oss.github.io/NimBus/';
const branch = 'master';

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'NimBus',
  tagline: 'Ordered integrations on Azure Service Bus, with visible recovery.',
  favicon: 'img/favicon.svg',

  url: 'https://context-and-oss.github.io',
  baseUrl: '/NimBus/docs/',
  trailingSlash: false,

  onBrokenLinks: 'throw',
  onBrokenAnchors: 'throw',

  i18n: { defaultLocale: 'en', locales: ['en'] },

  markdown: {
    // The docs are plain Markdown that must also render on GitHub. 'detect'
    // parses .md as CommonMark, so `{` and `<` in prose are not read as JSX.
    format: 'detect',
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'throw',
      onBrokenMarkdownImages: 'throw',
    },
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          path: '../docs',
          routeBasePath: '/',
          sidebarPath: './sidebars.js',
          exclude: excludeGlobs,
          // Keep file names such as adr/001-session-based-ordering in the URL.
          numberPrefixParser: false,
          editUrl: `${repoUrl}/edit/${branch}/docs/`,
          beforeDefaultRemarkPlugins: [
            [remarkRepoLinks, { repoRoot, repoUrl, branch, isServedByPortal }],
          ],
        },
        blog: false,
        pages: false,
        theme: { customCss: './src/css/custom.css' },
      }),
    ],
  ],

  themes: [
    '@docusaurus/theme-mermaid',
    [
      '@easyops-cn/docusaurus-search-local',
      {
        hashed: true,
        indexBlog: false,
        docsRouteBasePath: '/',
        docsDir: '../docs',
        highlightSearchTermsOnTargetPage: true,
        explicitSearchResultPath: true,
      },
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: `${websiteUrl}social-card.png`,
      colorMode: { respectPrefersColorScheme: true },
      docs: { sidebar: { hideable: true } },
      tableOfContents: { minHeadingLevel: 2, maxHeadingLevel: 3 },
      navbar: {
        title: 'NimBus',
        logo: { alt: 'NimBus', src: 'img/favicon.svg' },
        items: [
          { type: 'docSidebar', sidebarId: 'docs', position: 'left', label: 'Docs' },
          { type: 'doc', docId: 'getting-started', position: 'left', label: 'Get started' },
          {
            type: 'dropdown',
            label: 'Reference',
            position: 'left',
            items: [
              { type: 'doc', docId: 'cli', label: 'CLI (nb)' },
              { type: 'doc', docId: 'sdk-api-reference', label: 'SDK API' },
              { type: 'doc', docId: 'webapp-rest-api', label: 'WebApp REST API' },
            ],
          },
          { type: 'doc', docId: 'adr/README', position: 'left', label: 'ADRs' },
          { href: websiteUrl, label: 'Website', position: 'right', target: '_self' },
          { href: `${repoUrl}/releases`, label: 'Releases', position: 'right' },
          { href: repoUrl, label: 'GitHub', position: 'right' },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              { label: 'Getting started', to: '/getting-started' },
              { label: 'Architecture', to: '/architecture' },
              { label: 'Building adapters', to: '/building-adapters' },
              { label: 'Deployment', to: '/deployment' },
            ],
          },
          {
            title: 'Reference',
            items: [
              { label: 'CLI (nb)', to: '/cli' },
              { label: 'SDK API', to: '/sdk-api-reference' },
              { label: 'WebApp REST API', to: '/webapp-rest-api' },
              { label: 'Architecture decisions', to: '/adr' },
            ],
          },
          {
            title: 'Project',
            items: [
              { label: 'Website', href: websiteUrl, target: '_self' },
              { label: 'GitHub', href: repoUrl },
              { label: 'Releases', href: `${repoUrl}/releases` },
              { label: 'Contributing', href: `${repoUrl}/blob/${branch}/CONTRIBUTING.md` },
            ],
          },
        ],
        copyright: 'NimBus is open source under the MIT License.',
      },
      prism: {
        theme: prismThemes.github,
        darkTheme: prismThemes.dracula,
        additionalLanguages: ['csharp', 'bash', 'powershell', 'json', 'yaml', 'sql', 'http'],
      },
      mermaid: { theme: { light: 'neutral', dark: 'dark' } },
    }),
};

module.exports = config;
