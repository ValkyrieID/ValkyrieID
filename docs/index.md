---
layout: home

hero:
  name: ValkyrieID
  text: Minimal OpenID Connect server
  tagline: A small, spec-compliant OpenID Connect / OAuth 2.0 identity server built on .NET.
  image:
    src: /logo.png
    alt: ValkyrieID logo, a winged shield
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: Configuration
      link: /reference/configuration

features:
  - title: Small footprint
    details: A single container with low memory use and minimal dependencies.
  - title: Spec compliant
    details: Passes the OpenID Foundation conformance plans (Basic OP, Config OP, RP-Initiated and Back-Channel Logout).
  - title: Simple to operate
    details: SQLite or PostgreSQL, automatic migrations, and health endpoints out of the box.
---

::: warning Pre-1.0
ValkyrieID hasn't reached 1.0 yet: configuration and APIs may change between 0.x releases. Read the [changelog](https://github.com/ValkyrieID/valkyrieid/blob/main/CHANGELOG.md) and [back up](/operations/backups) before upgrading.
:::
