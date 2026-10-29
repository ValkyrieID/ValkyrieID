# Conformance testing

ValkyrieID targets the OpenID Foundation [conformance suite](https://openid.net/certification/about-conformance-suite/) plans **Basic OP**, **Config OP**, **RP-Initiated Logout**, and **Back-Channel Logout**. This page describes how to run them against a ValkyrieID instance.

## What you need

- A ValkyrieID instance reachable by the suite over HTTPS with a certificate the suite trusts, and with `Issuer__Url` set to that public URL.
  - With the **hosted suite** at <https://www.certification.openid.net>, the instance must be reachable from the internet, e.g. a test deployment or a tunnel to your machine.
  - With a **local suite**, the instance must be reachable from the suite's Docker containers.
- The same configuration as the server (`Database__*`, `MasterKey__Current`, `Issuer__Url`) in the shell where you run the setup script, so it writes to the instance's database.

Use a dedicated database. The setup creates a test user and four clients.

## 1. Register the test user and clients

```sh
# Hosted suite
conformance/setup.sh https://www.certification.openid.net valkyrieid

# Local suite
conformance/setup.sh https://localhost.emobix.co.uk:8443 valkyrieid
```

On Windows use `conformance/setup.ps1 -Suite <url> -Alias <alias>`.

The script:

- creates the user `conformance` with a random password, and prints it;
- creates the confidential clients `conformance-1` and `conformance-2` with the redirect URI `<suite>/test/a/<alias>/callback` and the scopes `openid profile email roles groups`;
- creates the confidential clients `conformance-logout-1` and `conformance-logout-2` for the logout plans, which also have the post-logout redirect URI `<suite>/test/a/<alias>/post_logout_redirect` and the back-channel logout URI `<suite>/test/a/<alias>/backchannel_logout`;
- writes the plan configurations to `conformance/out/`: `basic-op.json`, `config-op.json`, `rp-initiated-logout.json`, and `backchannel-logout.json`.

The clients are created with `--no-pkce` because the plans don't send PKCE parameters, and with `--allow-secret-post` so the plans can be repeated with `client_secret_post`. `conformance/out/` contains client secrets and is ignored by git. Delete it, and the clients (`client delete conformance-1`), when you're done.

## 2. Run the plans

1. Open the suite and choose **Create a new test plan**.
2. Select **OpenID Connect Core: Basic Certification Profile Authorization server test** with:
   - Server metadata location: `discovery`
   - Client registration type: `static_client`
   - Client authentication type: `client_secret_basic` (repeat with `client_secret_post`)
   - Response mode: `default` (repeat with `form_post` if you want to cover it)
3. Paste `conformance/out/basic-op.json` into the configuration JSON and create the plan.
4. Run each test. When the suite opens the login page, sign in as `conformance`.
5. Create a second plan, **OpenID Connect Core: Config Certification Profile Authorization server test**, with `conformance/out/config-op.json`.
6. Create a plan **OpenID Connect Core: RP-Initiated Logout Certification Profile Authorization server test** with the same options as Basic OP and `conformance/out/rp-initiated-logout.json`.
7. Create a plan **OpenID Connect Core: Back-Channel RP-Initiated Logout Certification Profile Authorization server test** with `conformance/out/backchannel-logout.json`. The suite must be reachable from ValkyrieID, because ValkyrieID posts logout tokens to the suite's back-channel logout URI.

When a logout test shows the ValkyrieID sign-out confirmation page, confirm it. ValkyrieID always asks a signed-in user before signing them out, even with a valid `id_token_hint`.

## Running the suite locally

The suite isn't published as a container image; build it from source:

```sh
git clone https://gitlab.com/openid/conformance-suite.git
cd conformance-suite
mvn clean package           # or the dockerized Maven build described in the suite's README
docker compose up
```

The suite is then available at <https://localhost.emobix.co.uk:8443> (the host name resolves to `127.0.0.1`). Its server container must reach ValkyrieID at the issuer URL, and must trust its certificate.

## Expected results

Some tests are skipped or produce warnings by design, because ValkyrieID advertises in discovery that it doesn't support the feature:

- request objects (`request`, `request_uri`): `request_parameter_supported` and `request_uri_parameter_supported` are `false`;
- the `claims` request parameter: `claims_parameter_supported` is `false`;
- the `address` and `phone` scopes, which aren't in `scopes_supported` and are ignored when requested;
- front-channel logout and session management (`check_session_iframe`), which aren't supported.

The test clients are allowed `offline_access` and the `refresh_token` grant, so the refresh token tests run.

Any other failure is a bug. Report it with the test log exported from the suite.