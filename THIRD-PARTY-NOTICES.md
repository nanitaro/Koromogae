# サードパーティの通知 (THIRD-PARTY-NOTICES)

このプロジェクト自身のコードは [MIT ライセンス](LICENSE) です。以下の同梱物は、それぞれ別のライセンスです。

## PortAudio

`portaudio.dll` (音声の入出力) を同梱しています。

- 本体: PortAudio (<https://github.com/PortAudio/portaudio>)。著作権者は Ross Bencina、Phil Burk のほか、Windows の実装に David Viens、Dmitry Kostjuchenko、Andrew Baldwin、Robert Marsanyi (下の「PortAudio の著作権者」)。ライセンスは **MIT 条件** (下の「PortAudio のライセンス」)。
- 入手経路: NuGet パッケージ `org.k2fsa.portaudio.runtime.win-x64` 1.0.6 (<https://github.com/csukuangfj/PortAudioSharp2>)。
  このパッケージは、PortAudio をビルドしたバイナリを再配布しており、パッケージのライセンスは **Apache-2.0** と表示されている。
- 同梱の DLL は `PortAudio V19.7.0-devel, revision 147dd722548358763a8b649b3e4b41dfffbcfbb6` (DLL に埋め込まれた文字列)。下の MIT 本文は、このリビジョンの `LICENSE.txt` と同じもの。
- どちらの条件にも従えるよう、MIT の本文と Apache-2.0 の本文の両方を載せている。本プロジェクトは PortAudio のソースを改変していない。

### PortAudio の著作権者 (同梱 DLL に含まれる実装のファイルの表示)

下の「PortAudio のライセンス」の本文は、PortAudio の `LICENSE.txt` そのままで、実装ファイル用の雛形 (`Copyright (c) 1999-2000 <author(s)>`) を含む。
実際の著作権者は、ファイルごとに次のとおり表示されている (同梱 DLL と同じリビジョン `147dd722548358763a8b649b3e4b41dfffbcfbb6` の公式ソースで確認。DLL には Windows の 4 つのホスト API = WASAPI・WDM-KS・DirectSound・MME が含まれ、ASIO・JACK は含まれない)。
どのファイルも、上と同じ MIT 条件のライセンス表示を持つ。

- 共通部分 (`include/portaudio.h`、`src/common/*`): Ross Bencina、Phil Burk (1999-2008。`pa_cpuload` は 2002 年の Ross Bencina、`pa_stream` は 2008 年の Ross Bencina、`pa_trace` は 1999-2000 年の Phil Burk)
- Windows 共通 (`src/os/win/*`、`include/pa_win_waveformat.h`): Ross Bencina、Phil Burk (1999-2011)。`pa_win_coinitialize` は Ross Bencina、**Dmitry Kostjuchenko** (1999-2011)。`pa_win_wdmks_utils` は **Andrew Baldwin**、Ross Bencina (1999-2007)。`pa_win_waveformat` と `pa_win_util` は Ross Bencina
- MME (`src/hostapi/wmme/pa_win_wmme.c`、`include/pa_win_wmme.h`): Ross Bencina、Phil Burk (1999-2000)
- DirectSound (`src/hostapi/dsound/*`、`include/pa_win_ds.h`): Ross Bencina、Phil Burk、**Robert Marsanyi** (1999-2007)
- WDM-KS (`src/hostapi/wdmks/pa_win_wdmks.c`、`include/pa_win_wdmks.h`): **Andrew Baldwin**、Ross Bencina、Phil Burk (1999-2007)
- WASAPI (`src/hostapi/wasapi/pa_win_wasapi.c`、`include/pa_win_wasapi.h`): **David Viens** (2006-2010)、**Dmitry Kostjuchenko** (2010-2019)、Ross Bencina、Phil Burk (1999-2019)

(太字は、上の本文の「Ross Bencina と Phil Burk」だけでは分からない著作権者。)
### PortAudio のライセンス

```
Portable header file to contain:
>>>>>
/*
 * PortAudio Portable Real-Time Audio Library
 * PortAudio API Header File
 * Latest version available at: http://www.portaudio.com
 *
 * Copyright (c) 1999-2006 Ross Bencina and Phil Burk
 *
 * Permission is hereby granted, free of charge, to any person obtaining
 * a copy of this software and associated documentation files
 * (the "Software"), to deal in the Software without restriction,
 * including without limitation the rights to use, copy, modify, merge,
 * publish, distribute, sublicense, and/or sell copies of the Software,
 * and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
 * ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
 * CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

/*
 * The text above constitutes the entire PortAudio license; however, 
 * the PortAudio community also makes the following non-binding requests:
 *
 * Any person wishing to distribute modifications to the Software is
 * requested to send the modifications to the original developer so that
 * they can be incorporated into the canonical version. It is also 
 * requested that these non-binding requests be included along with the 
 * license above.
 */
<<<<<
 

Implementation files to contain:
>>>>>
/*
 * PortAudio Portable Real-Time Audio Library
 * Latest version at: http://www.portaudio.com
 * <platform> Implementation
 * Copyright (c) 1999-2000 <author(s)>
 *
 * Permission is hereby granted, free of charge, to any person obtaining
 * a copy of this software and associated documentation files
 * (the "Software"), to deal in the Software without restriction,
 * including without limitation the rights to use, copy, modify, merge,
 * publish, distribute, sublicense, and/or sell copies of the Software,
 * and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
 * MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
 * IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR
 * ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
 * CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 */

/*
 * The text above constitutes the entire PortAudio license; however, 
 * the PortAudio community also makes the following non-binding requests:
 *
 * Any person wishing to distribute modifications to the Software is
 * requested to send the modifications to the original developer so that
 * they can be incorporated into the canonical version. It is also 
 * requested that these non-binding requests be included along with the 
 * license above.
 */
<<<<<
```

### NuGet パッケージのライセンス表示 (Apache-2.0)

パッケージ `org.k2fsa.portaudio.runtime.win-x64` が表示しているライセンス。本文は <https://www.apache.org/licenses/LICENSE-2.0.txt> のもの。

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

## .NET の起動用プログラム (apphost) と .NET ランタイム

`Koromogae.exe`、`PluginHost64\VstPluginHost.exe`、`PluginHost32\VstPluginHost.exe` は、.NET SDK が実行ファイルとして組み込む **apphost** (.NET の起動用プログラム。インストール済みの .NET ランタイムを探して、アプリの DLL を起動する) を使っている。
apphost は .NET (dotnet/runtime) の一部で、**MIT ライセンス**である。Microsoft の説明 (<https://github.com/dotnet/core/blob/main/license-information-windows.md>) は、Windows 版の .NET で個別に挙げた DLL (coreclr.dll など) 以外の「すべてのバイナリとファイルは MIT ライセンス」としている。
また、実行ホストのように、.NET ランタイムの一部はアプリに埋め込まれる、とも説明している (<https://github.com/dotnet/core/blob/main/license-information.md>)。
著作権表示と許諾文 (<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> と同じもの):

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

.NET の第三者通知 (dotnet/runtime): <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>。インストール済みの .NET ランタイムのフォルダにも `ThirdPartyNotices.txt` がある。
apphost に、この通知の第三者のコードが含まれるかどうかは、バイナリからは確認できていない。

.NET **ランタイム本体は同梱していない**。本体 (x64) と 64bit のプラグインホストは x64 版、32bit のプラグインホスト (`PluginHost32\`) は x86 版の、利用者がインストールした .NET 10 ランタイムを使う。
ランタイムのライセンスは Microsoft の説明のとおり: ソースは MIT ライセンス (<https://github.com/dotnet/runtime>)、Windows 版の配布物は、一部のファイルが **.NET Library License** (<https://dotnet.microsoft.com/dotnet_library_license.htm>)、そのほかは MIT ライセンスである。

## VST (Steinberg)

VST is a registered trademark of Steinberg Media Technologies GmbH.
(VST は Steinberg Media Technologies GmbH の登録商標です。)

本アプリは、Steinberg Media Technologies GmbH とは関係がなく、同社が承認・提供するものではない。
(This application is not affiliated with, endorsed by, or sponsored by Steinberg Media Technologies GmbH.)

### VST3 SDK

VST3 プラグインを読み込むためのインターフェース (IID・メソッドの順序・構造体の配置) は、Steinberg が公開している VST 3 のインターフェース定義
(`vst3_pluginterfaces`、MIT ライセンス、Copyright (c) Steinberg Media Technologies GmbH、<https://github.com/steinbergmedia/vst3_pluginterfaces>) を参照して、C# で実装している。
**VST3 SDK** のソースファイルは、この配布物にも、本アプリのソースにも含まれていない。

### VST2 SDK

VST2 プラグインを読み込むためのインターフェース (構造体の配置・オペコードの定数) は、C# で自前に記述している。
**VST2 SDK** のファイル (`aeffect.h`、`aeffectx.h`) は、この配布物にも、本アプリのソースにも含まれていない。
