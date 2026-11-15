// Runs before every test file (bunfig.toml). Every API is mocked in tests, so strip any real credentials
// bun loaded from .env: a test that forgets a mock then fails on "no credentials" instead of calling a paid API.
for (const key of ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ELEVENLABS_API_KEY", "REACTOR_API_KEY"]) {
  delete process.env[key];
}
