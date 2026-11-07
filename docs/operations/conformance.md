# Conformance testing

ValkyrieID is tested with the OpenID Foundation [conformance suite](https://openid.net/certification/about-conformance-suite/) plans **Basic OP**, **Config OP**, **RP-Initiated Logout**, and **Back-Channel Logout**. The suite runs next to ValkyrieID in Docker, both in CI and on your machine, and every plan runs unattended. The latest results are published on [Conformance results](/conformance/results).

## Run it in Docker

You need Docker (or Podman behind the Docker CLI) with Compose; Python isn't needed on the host. From the repository root:

```sh
conformance/run.sh            # the four plans
conformance/run.sh --full     # also client_secret_post and form_post (release tags)
```

On Windows use `conformance/run.ps1` (`-Full`, `-Image`, `-Keep`). The script:

1. builds the ValkyrieID image from your checkout (or uses `--image <image>`);
2. starts `conformance/docker-compose.yml`: the suite (`registry.gitlab.com/openid/conformance-suite:release-v5.3.4`, its nginx, and MongoDB) and ValkyrieID;
3. registers the test user and clients with `conformance/setup.sh --docker`;
4. runs the plans with the suite's `scripts/run-test-plan.py` (`conformance/run-plans.py`), including browser automation that signs in and confirms the sign-out;
5. checks the results against `conformance/expected-results.json`, and removes the containers (keep them with `--keep`).

The run fails if a test fails, or if a warning, review, or skipped result isn't listed in `expected-results.json` with its reason, or if a listed result no longer happens.

Results are written to `conformance/out/results/`: one zip per plan (the suite's signed export with the full test logs), `summary.md`, and the ValkyrieID and suite logs. `conformance/out/` also holds the client secrets and the test user's password of the throwaway instance, and is ignored by git.

### How the setup works

- The suite is at `https://localhost.emobix.co.uk:8443` and ValkyrieID at `https://valkyrieid.localtest.me:9443`, with TLS served by Kestrel. Both names resolve to `127.0.0.1` on your machine and to the containers through network aliases, so the same URLs work from the browser automation, from ValkyrieID's back-channel logout requests, and from your browser while the containers run.
- A one-off container creates a throwaway CA and the certificates for both names, and deletes the CA key. ValkyrieID trusts the CA through `SSL_CERT_FILE` for its back-channel logout requests to the suite.
- The rate limits are raised through environment variables, because the suite signs in far more often than a person; everything else runs with the defaults.
- Nothing is kept: the database, keys, and certificates are removed with the containers.

## Run against the hosted suite

You can also run the plans by hand on the hosted suite at <https://www.certification.openid.net>, against a ValkyrieID instance that is reachable from the internet over HTTPS with a trusted certificate.

1. In a shell with the server's configuration (`Database__*`, `MasterKey__Current`, `Issuer__Url` set to its public URL), run `conformance/setup.sh https://www.certification.openid.net valkyrieid` (on Windows `conformance/setup.ps1 -Suite <url> -Alias <alias>`). Use a dedicated database.
2. Create the plans in the suite with the configuration files from `conformance/out/`:

| Plan | Configuration | Variants |
|---|---|---|
| OpenID Connect Core: Basic Certification Profile Authorization server test | `basic-op.json` | `discovery`, `static_client` |
| OpenID Connect Core: Config Certification Profile Authorization server test | `config-op.json` | |
| OpenID Connect Core: RP-Initiated Logout Certification Profile Authorization server test | `rp-initiated-logout.json` | `code`, `static_client` |
| OpenID Connect Core: Back-Channel RP-Initiated Logout Certification Profile Authorization server test | `backchannel-logout.json` | `code`, `static_client` |

3. Run each test. Sign in as `conformance` with the password the script printed, and confirm the sign-out when ValkyrieID asks. Upload screenshots where the suite asks for them.

The setup creates:

- the user `conformance`, with a random password;
- the confidential clients `conformance-1` and `conformance-2` with the redirect URI `<suite>/test/a/<alias>/callback` and the scopes `openid profile email roles groups offline_access`;
- `conformance-rp-logout-1` and `-2` for the RP-Initiated Logout plan, which also have the post-logout redirect URI `<suite>/test/a/<alias>/post_logout_redirect`;
- `conformance-logout-1` and `-2` for the Back-Channel Logout plan, which also have the back-channel logout URI `<suite>/test/a/<alias>/backchannel_logout`. Only these get a back-channel logout URI, because the other plans fail when they receive an unexpected logout token.

The clients are created with `--no-pkce` because the plans don't send PKCE parameters, and with `--allow-secret-post` for the `client_secret_post` tests. Delete the clients (`client delete conformance-1`) and `conformance/out/` when you're done.

## Expected results

Some results aren't a pass by design. Each one is listed with its reason in `conformance/expected-results.json` and on [Conformance results](/conformance/results):

- **Skipped:** request objects (`request_parameter_supported` is `false`) and the `address` and `phone` scopes, which aren't supported.
- **Review:** tests that upload a screenshot for a person to check: the second sign-in page (`prompt=login`, `max_age`), the error page for an unregistered redirect URI, and the sign-out pages. ValkyrieID always asks a signed-in user before signing them out, and shows that the application's request was invalid when it doesn't redirect back.
- **Warnings:** profile claims that aren't stored, scope claims also in the ID token, no `acr` claim, access tokens that stay valid after a code is reused, and the `claims` parameter, which isn't supported.

Any other result is a bug. Report it with the test log from the results zip.

## Certification status

ValkyrieID isn't officially [OpenID Certified](https://openid.net/certification/), because certification requires a paid OpenID Foundation membership. Instead, every release must pass all four plans with no failures, and the results are published. Published results describe ValkyrieID as passing the OpenID conformance suite, not as "OpenID Certified".
