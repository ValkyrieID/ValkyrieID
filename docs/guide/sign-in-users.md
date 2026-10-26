# Sign in users

This guide signs users in to an app with the authorization code flow and PKCE.

## ASP.NET Core web app

Register a confidential client:

```sh
valkyrieid client create webapp --grant authorization_code \
  --redirect-uri https://app.example.com/signin-oidc \
  --scope openid --scope profile --scope email
```

Configure the app with the printed secret:

```csharp
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        options.Authority = "https://id.example.com";
        options.ClientId = "webapp";
        options.ClientSecret = builder.Configuration["Oidc:ClientSecret"];
        options.ResponseType = "code";
        options.UsePkce = true;
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "preferred_username";
    });
```

The ID token already contains the profile and email claims, so calling userinfo is optional.

## Single-page app

Register a public client. Public clients have no secret and always use PKCE:

```sh
valkyrieid client create spa --public --grant authorization_code \
  --redirect-uri https://spa.example.com/callback \
  --cors-origin https://spa.example.com \
  --scope openid --scope profile --scope orders.read
```

`--cors-origin` lets the browser call the token and userinfo endpoints. With [oidc-client-ts](https://github.com/authts/oidc-client-ts):

```ts
const userManager = new UserManager({
  authority: 'https://id.example.com',
  client_id: 'spa',
  redirect_uri: 'https://spa.example.com/callback',
  response_type: 'code',
  scope: 'openid profile orders.read',
})
```

The access token's audience is the API resources that own the requested API scopes (here `orders-api`). See [Protect an API](/guide/protect-an-api).

## Native and CLI apps

Register a loopback redirect URI such as `http://127.0.0.1/callback`. Any port is accepted at sign-in, so the app can listen on an ephemeral port.
