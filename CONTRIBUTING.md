# Contributing to Hatifect

Thank you for taking the time to report a problem or propose an improvement.
Hatifect is source-available under the **Hatifect Personal Use License 2.0**. It
is not an open-source project, and contributions do not change the license that
applies to the rest of the project.

This document summarizes the contribution workflow. The `LICENSE` file is the
controlling legal text, especially Sections 3, 4, 6, and 9.

## Decision authority

`ihatectf` alone decides whether, when, and in what form a contribution is
accepted. A report or pull request may be modified, rejected, or closed without
an obligation to merge it, continue development, or provide a reason.

Only `ihatectf`, or a person expressly authorized by `ihatectf`, may merge a
pull request. Access to a feature branch does not include merge authority.

## Reporting an issue

Before opening a new issue, check whether the same problem has already been
reported. A public issue may contain:

- a clear description and reproduction steps;
- expected and actual behavior;
- reasonably limited diagnostic logs;
- screenshots; and
- the smallest code excerpt needed to explain the problem.

Remove passwords, tokens, personal data, private paths, and unrelated
confidential information. Do not attach full modified source files, complete
patches, substantial portions of Hatifect, or distributable builds to an issue.
Use a pull request for proposed code changes.

## Security vulnerabilities

Do not report an unpatched vulnerability in a public issue. Use GitHub's private
vulnerability-reporting feature in the Official Repository when available, or
another private contact method identified by `ihatectf` in the repository.

Public disclosure is allowed after the first of the following events:

- `ihatectf` releases a fix;
- `ihatectf` gives written authorization; or
- 90 days pass after receipt of a sufficiently detailed private report.

Disclose only what is reasonably necessary to explain and verify the issue.
Section 9 of the `LICENSE` file controls if this summary differs from it.

## Proposing a change

1. Create a fork of the Official Repository, or use a feature branch for which
   `ihatectf` has expressly granted access.
2. Create focused commits on that fork or feature branch. Do not commit directly
   to a protected primary branch.
3. Follow the project's existing style and conventions.
4. Test the affected behavior and describe the tests in the pull request.
5. Identify every added or copied third-party component, its source, and its
   license. Do not add code with unknown or incompatible licensing.
6. Open a pull request against the Official Repository and explain the purpose,
   behavior change, limitations, and third-party licensing impact.

The license permits public GitHub forks, branches, commits, and pull requests
only for this contribution workflow. It does not permit a separate release,
mirror, package, patch archive, binary distribution, or publication on another
service.

## Contributor rights and grant

You retain copyright in original material you author.

By intentionally submitting a contribution, you confirm that:

- you have the right to submit it;
- it does not knowingly disclose another person's confidential information;
- third-party material and its license terms are clearly identified; and
- you grant the limited review rights stated in Section 6.1 of `LICENSE`.

You may withdraw an unmerged contribution by closing the pull request and
clearly notifying `ihatectf` before merge. GitHub may retain historic records as
permitted by its own terms.

If the contribution is merged, it becomes an Accepted Contribution. At that
moment you grant the complete copyright and patent licenses in Section 6.2 of
`LICENSE`, including the right for `ihatectf` to modify, commercialize,
sublicense, and relicense the accepted material under any terms. That grant is
perpetual and irrevocable. It is a license, not a transfer of your copyright.

Reasonable author attribution will be retained through Git commit metadata.
Commits may be squashed, rebased, edited, or combined, and no separate
`CONTRIBUTORS` listing is promised.

## Third-party code

Do not assume that publicly visible code can be copied. Before submitting any
third-party code, asset, generated material, or dependency:

- identify its exact source and version;
- provide its SPDX license identifier when one exists;
- preserve all required copyright, license, attribution, and NOTICE text;
- describe any modifications; and
- explain why its license is compatible with Hatifect's distribution model.

GPL- or AGPL-covered code must not be included in a combined or derivative work
without prior written approval from `ihatectf` and a separate compatibility
review. Listing a component in `THIRD_PARTY_NOTICES` does not cure an
incompatible license.
