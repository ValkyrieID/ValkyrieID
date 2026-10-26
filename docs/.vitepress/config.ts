import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'ValkyrieID',
  description: 'A minimal, lightweight OpenID Connect / OAuth 2.0 identity server for .NET',
  lang: 'en-US',
  cleanUrls: true,
  lastUpdated: true,
  themeConfig: {
    nav: [
      { text: 'Guide', link: '/guide/getting-started' },
      { text: 'Reference', link: '/reference/configuration' },
    ],
    sidebar: [
      {
        text: 'Guide',
        items: [
          { text: 'Getting started', link: '/guide/getting-started' },
          { text: 'First run', link: '/guide/first-run' },
          { text: 'Protect an API', link: '/guide/protect-an-api' },
        ],
      },
      {
        text: 'Reference',
        items: [
          { text: 'Configuration', link: '/reference/configuration' },
          { text: 'Endpoints', link: '/reference/endpoints' },
          { text: 'CLI', link: '/reference/cli' },
          { text: 'Health endpoints', link: '/reference/health' },
        ],
      },
      {
        text: 'Operations',
        items: [
          { text: 'Master encryption key', link: '/operations/master-key' },
          { text: 'Signing key rotation', link: '/operations/key-rotation' },
          { text: 'Observability', link: '/operations/observability' },
        ],
      },
    ],
    socialLinks: [{ icon: 'github', link: 'https://github.com/ValkyrieID/valkyrieid' }],
    search: { provider: 'local' },
    editLink: {
      pattern: 'https://github.com/ValkyrieID/valkyrieid/edit/main/docs/:path',
    },
    footer: { message: 'Released under the MIT License.' },
  },
})
