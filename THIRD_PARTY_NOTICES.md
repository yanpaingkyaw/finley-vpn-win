# Third-party notices

Finley's own source code is made available under the MIT License in `LICENSE`. Third-party components are subject to their own licenses; the MIT License does not replace their terms.

## sing-box 1.14.4

Finley's Windows installer includes the unmodified sing-box engine from SagerNet. sing-box is licensed under GPL-3.0-or-later and its license notice includes an additional condition about use of its name and implied association. See `bin/SING-BOX-LICENSE.txt` and `build/GPL-3.0-or-later.txt`.

- Upstream source and build instructions: https://github.com/SagerNet/sing-box/tree/v1.14.4
- Upstream release: https://github.com/SagerNet/sing-box/releases/tag/v1.14.4
- Upstream revision reported by the bundled binary: `c005f35300ce76427dc285481ec5695f5b179cd2`
- Bundled `sing-box.exe` SHA-256: `8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9`
- The Finley release also provides `sing-box-v1.14.4-source.tar.gz`, obtained from the upstream v1.14.4 tag, and the GPL license text.

Finley is an independent project and is not represented as affiliated with or endorsed by sing-box, SagerNet, or Outline.

Other dependencies and embedded third-party components retain their applicable licenses. Review their notices and the license implications of distributing a combined installer before redistribution.

## Embedded Wintun 0.14.1

The upstream sing-tun revision used to build the bundled sing-box includes the Wintun 0.14.1 prebuilt Windows driver library from WireGuard LLC. The upstream AMD64 DLL matches the 0.14.1 distribution. Its separate prebuilt-binary terms are in `build/WINTUN-LICENSE.txt`; the release includes the original `wintun-0.14.1.zip` distribution and `sing-tun-source.tar.gz` for revision `040db71fcd33196f1d15708b91eb61e9dc6ab8dd`.

- Wintun: https://www.wintun.net/
- sing-tun source: https://github.com/SagerNet/sing-tun/tree/040db71fcd33196f1d15708b91eb61e9dc6ab8dd
