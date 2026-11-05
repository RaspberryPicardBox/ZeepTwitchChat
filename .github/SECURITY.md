# Reporting a security issue

Please do **not** open a public issue for a security problem. Use GitHub's private reporting:

1. Go to the **Security** tab of this repository.
2. Choose **Report a vulnerability**.

The kind of thing worth reporting for this mod is anything that would let something arriving from
the network - a chat message, a provider's emote list, an image - run code, read files, or grow
unbounded in the game process. `docs/security.md` describes the current threat model, what has
already been checked, and the residual risks, so it is worth a read before reporting.

Expect an acknowledgement within a few days. Please give me a reasonable window to ship a fix
before publishing details.
