# Security policy

AI Meter reads sign-in tokens that other apps keep on your PC, and stores API keys you add in Settings. Security reports are taken seriously.

## Supported versions

Only the [latest release](https://github.com/dichiara19/ai-meter/releases/latest) receives security fixes.

## Reporting a vulnerability

**Please don't open a public issue.** Instead, report it privately through GitHub: go to the repository's **Security** tab and choose **Report a vulnerability** ([direct link](https://github.com/dichiara19/ai-meter/security/advisories/new)).

Please include:
- what an attacker could do;
- the steps to reproduce it;
- the version of AI Meter and of Windows.

Never include real tokens or keys.

You will get an answer within a week. Once the issue is fixed, the advisory is published, crediting you unless you prefer otherwise.

## Scope

Of particular interest:
- how tokens and API keys are read, stored and sent (only to the service they belong to, never refreshed or stored for other apps);
- loading of service definitions from `%APPDATA%\AIMeter\providers`;
- requests to local model servers on `127.0.0.1`;
- the build and release pipeline in `.github/workflows`.

Out of scope:
- issues that need an attacker who already has full control of the user's account on the PC;
- social engineering.
