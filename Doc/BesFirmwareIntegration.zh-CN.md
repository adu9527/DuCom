# 恒玄接入文档 — 校验码查询工具

本文件随 DuCom 内嵌发布；在“插件管理 → 校验码查询工具 → 恒玄接入文档”中离线查看。
文档及代码可以全选、复制。无需联网，无需在电脑上额外安装文档查看器。

## 1. 支持范围与选择

- 杰理 FW/UFW、蓝讯 PRD/UPD：直接选择固件查询，不要求修改固件工程。
- 恒玄 BES：需要固件保留有效的 sys_build_info。已有 CRC32_OF_IMAGE 的旧固件可读取原厂 CRC；
  若需要“相同源码与构建配置得到相同版本码”，按下面步骤增加 FIRMWARE_CODE 和 BUILD_SHA256。
- 本插件读取固件文件，不读取设备当前 NV 名称。BT_NAME 是固件默认名称。
- 芯片、厂商由内容结构识别，改变文件名或后缀不改变结果。

本指南交付一个可复制的“构建前生成定长头文件”接入例子：复用原 build_info，
不新增设备函数、线程或 NV 操作；需要源码、编译器、目标与实际构建流程的项目级适配。
它与“链接后按 ELF 定位预留槽再填充”的方案是两种实现，不要混用。
此例在 PC 侧做过生成与解析验证，不代表你的 SDK 已完成板级或量产验证。

## 2. 三种校验值的区别

FIRMWARE_CODE = CRC32(bytes.fromhex(BUILD_SHA256))，8 位大写十六进制。
BUILD_SHA256 = 固定规则下，工程输入文件、有效配置和工具链标识的 SHA-256。
CRC32_OF_IMAGE = SDK 原有 OTA 完整性字段，继续由 SDK 原流程生成。

FIRMWARE_CODE 用于快速核对版本；本次真实 BUILD_DATE 改变时，版本码可以不变，
但 BIN SHA-256、OTA CRC 可以变化。不要用版本短码替换 OTA CRC 或产测协议中的 CRC。
32 位短码可能碰撞；需要严格核对时同时保存完整 BUILD_SHA256 和交付 BIN SHA-256。

## 3. 接入前确认位置

1. 找到 SDK 的 utils/build_info/build_info.c（不同 SDK 路径可能不同）。
2. 找到 sys_build_info 字符串及现有 CHIP、KERNEL、BUILD_DATE、CRC32_OF_IMAGE。
3. 从 ELF/map 确认 build_info 被链接保留，所在只读段及 Flash/RAM 占用。
4. 找到产品主固件的正式构建入口、objcopy、地址填充、CRC、压缩与签名的顺序。
5. 确认 CRC32_OF_IMAGE 的填值工具是否真的存在：部分 SDK 的打包脚本只写入占位
   0x00000000（如 printf 'CRC32_OF_IMAGE=0x00000000\n'），负责计算填值的脚本
   （如 generate_crc32_of_image.py）可能已缺失；缺则按 12.1 的范围补齐，
   否则压缩 OTA 在本插件中会显示校验不一致。
6. 保存原始构建命令、产物及 map，用独立开发分支接入。

不要把生成头文件接入 PROGRAMMER、ROM、CP 或传感器等辅助镜像，除非单独验证。
不要向 BIN 末尾追加字段；部分 BES 工具从文件最后 4 字节读取加载地址。

## 4. 创建配置

在 SDK 根目录新建 tools/fw_identity/policy.json。以下路径仅是例子，必须按真实工程修改。
inputs 是纳入版本身份的文件或目录；整个目录下所有普通文件都计入，包括注释和资源。
generated、输出、版本控制与日志目录应保持在排除列表中，避免生成结果反过来影响身份。
工具链摘要文件放在受控输入范围外，并通过 toolchain 参数单独计入。

```json
{
  "target": "your_product_main",
  "bluetooth_name": "Your Headset",
  "inputs": ["apps", "services", "platform", "config", "utils", "Makefile"],
  "exclude": [".git", "out", "build", "release", "delivery", "logs", "generated", "__pycache__"],
  "effective_options": ["T=your_product_main", "DEBUG=0", "BLE=1", "AUDIO_CODEC=your_codec"],
  "toolchain": "tools/fw_identity/toolchain.txt"
}
```

inputs 必须覆盖实际参与构建的链接脚本、预编译库、配置、音源/模型和构建脚本。
policy.json 自身也会计入摘要，toolchain.txt 会额外按原始字节计入。
不要把时间、绝对路径、用户名、-j 并发数写入 effective_options。
有效参数保持实际覆盖顺序，特别是重复的 -D/-I；不要为了稳定而随意排序。

SDK 里按芯片切换的目录常用软链实现（如 bthost、multimedia/algorithms 指向
bthost_best1307p、algorithms_best1307p 一类真实目录）。inputs 应直接列出软链
指向的真实目录，并在 exclude 中排除软链名与未参与构建的同名变体
（*_best1307ph、*_generic），避免修改未参与构建的目录也改变版本码。
WSL 下用 find <目录> -type l 排查软链；Windows 侧 PowerShell 对 LX 软链不可见，
不要用它确认软链是否存在。另外 /mnt/c 上的 NTFS 大小写不敏感，
仅大小写不同的两个文件名是同一个文件。

## 5. 完整 Python 生成脚本

保存为 tools/fw_identity/prepare.py，使用 Python 3.10+。
脚本只生成 PC 文件，不修改 SDK 业务源码，不调用烧录工具。
默认生成文件放在 generated/fw_identity.h 和 generated/fw_identity.json。
建议将生成目录加入 .gitignore，并在 inputs 扫描时排除。

```python
import argparse
import binascii
import hashlib
import json
from pathlib import Path


def framed(hash_object, value):
    hash_object.update(len(value).to_bytes(8, "big"))
    hash_object.update(value)


def prepare(root, policy_path):
    root = root.resolve(strict=True)
    policy_path = policy_path.resolve(strict=True)
    policy_bytes = policy_path.read_bytes()
    policy = json.loads(policy_bytes)
    excluded = set(policy["exclude"])
    paths = {}
    for entry in policy["inputs"]:
        base = root / entry
        if not base.exists():
            raise ValueError(f"输入路径不存在: {entry}")
        candidates = [base] if base.is_file() else base.rglob("*")
        for path in candidates:
            relative = path.relative_to(root)
            if any(part in excluded for part in relative.parts):
                continue
            # 不接受未经确认的软链接/目录联接，避免漏算 SDK 外部输入。
            chain = [root / Path(*relative.parts[:i])
                     for i in range(1, len(relative.parts) + 1)]
            if any(p.is_symlink() or getattr(p, "is_junction", lambda: False)()
                   for p in chain):
                raise ValueError(f"请显式处理符号链接/目录联接: {relative}")
            if path.is_file():
                paths[relative.as_posix()] = path
    # 明确把本生成器和策略纳入身份，规则改动也会改变版本码。
    generator = Path(__file__).resolve(strict=True)
    for path in [policy_path, generator]:
        paths[path.relative_to(root).as_posix()] = path
    toolchain = root / policy["toolchain"]
    if not toolchain.is_file():
        raise ValueError("请先生成 toolchain.txt，记录真实使用的工具链和二进制摘要")
    paths[toolchain.relative_to(root).as_posix()] = toolchain
    digest = hashlib.sha256()
    framed(digest, b"DUCOM-BES-IDENTITY-1")
    configuration = json.dumps({"target": policy["target"],
                                "effective_options": policy["effective_options"]},
                               sort_keys=True, ensure_ascii=False,
                               separators=(",", ":")).encode("utf-8")
    framed(digest, configuration)
    records = []
    for relative, path in sorted(paths.items()):
        data = path.read_bytes()
        sha = hashlib.sha256(data).digest()
        framed(digest, relative.encode("utf-8"))
        framed(digest, len(data).to_bytes(8, "big"))
        framed(digest, sha)
        records.append({"path": relative, "bytes": len(data), "sha256": sha.hex().upper()})
    build = digest.hexdigest().upper()
    code = f"{binascii.crc32(bytes.fromhex(build)) & 0xFFFFFFFF:08X}"
    name = policy.get("bluetooth_name", "")
    encoded_name = name.encode("utf-8")
    if len(encoded_name) > 63 or any(byte < 32 or byte == 127 for byte in encoded_name):
        raise ValueError("默认蓝牙名必须是无控制字符、最多 63 字节的 UTF-8 字符串")
    # 十六进制转义逐字节编码中文；每次固定 63 字节，防止名称长短改变扩展容量。
    padded_name = encoded_name + b" " * (63 - len(encoded_name))
    name_c = "".join(f"\\x{byte:02X}" for byte in padded_name)
    header = (
        "/* Generated by prepare.py; do not edit. */\n"
        "#ifndef DUCOM_FW_IDENTITY_H\n#define DUCOM_FW_IDENTITY_H\n"
        f'#define DUCOM_FW_BUILD_SHA "{build}"\n'
        f'#define DUCOM_FW_CODE "{code}"\n'
        f'#define DUCOM_FW_BT_NAME "{name_c}"\n'
        "#endif\n"
    )
    report = {"policy": "DUCOM-BES-IDENTITY-1", "target": policy["target"],
              "build_sha256": build, "firmware_code": code, "inputs": records}
    return header, report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--policy", type=Path, required=True)
    parser.add_argument("--verify", action="store_true")
    args = parser.parse_args()
    header, report = prepare(args.root, args.policy)
    generated = args.root / "generated"
    header_path = generated / "fw_identity.h"
    report_path = generated / "fw_identity.json"
    if args.verify:
        if header_path.read_bytes() != header.encode("ascii"):
            raise SystemExit("构建期间输入或策略发生变化，拒绝交付；重新生成并重新构建")
        if json.loads(report_path.read_text(encoding="utf-8")) != report:
            raise SystemExit("身份清单不匹配，拒绝交付")
        print("输入复核通过:", report["firmware_code"])
        return
    generated.mkdir(parents=True, exist_ok=True)
    encoded = header.encode("ascii")
    # 内容没变时不刷新头文件 mtime，让正常依赖追踪决定是否重编译。
    if not header_path.exists() or header_path.read_bytes() != encoded:
        header_path.write_bytes(encoded)
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("FIRMWARE_CODE=" + report["firmware_code"])
    print("BUILD_SHA256=" + report["build_sha256"])


if __name__ == "__main__":
    main()
```

## 6. 工具链清单与实际构建参数

在 Linux/WSL 的 SDK 构建环境中，用真实工具名生成清单，例如：

```sh
{
  arm-none-eabi-gcc --version
  arm-none-eabi-g++ --version
  arm-none-eabi-ld --version
  arm-none-eabi-objcopy --version
  # 只记录内容摘要，不记录电脑相关的绝对路径。
  for tool in arm-none-eabi-gcc arm-none-eabi-g++ arm-none-eabi-ld arm-none-eabi-objcopy; do
    printf '%s ' "$tool"
    sha256sum "$(command -v "$tool")" | cut -d ' ' -f 1
  done
} > tools/fw_identity/toolchain.txt
```

Windows 可使用对应工具的 --version 与 Get-FileHash；不要把带绝对路径的输出直接计入身份。
SDK 若使用包装器，还需记录包装器、实际编译器、链接器及相关插件，不能只记录 GCC 首行版本。

建议把清单生成固化成 tools/fw_identity/gen_toolchain.sh（遍历 gcc/g++/ld/objcopy/
ar/nm/objdump/size，各记录 --version 首行与 sha256），在 WSL 登录 shell 中执行
（wsl -e bash -lc ...，工具链 PATH 常只存在于登录环境，非登录 shell 会找不到编译器）。
清单不含机器路径，同一工具链跨机器生成结果一致，可以提交版本库：新环境开箱即可构建；
工具链升级时清单内容变化随提交留痕，版本码随之变化是预期而非异常。

## 7. 在原 build_info 中加入字段

先让 build_info.c 能 include SDK 根目录 generated/fw_identity.h。
通过该 SDK 现有产品级编译选项加入 -I<SDK根目录>/generated 和 -DDUCOM_FW_IDENTITY。
不要把这个宏无条件加到所有辅助目标。

在 build_info.c 顶部加入：

```c
#ifdef DUCOM_FW_IDENTITY
#include "fw_identity.h"
#define DUCOM_IDENTITY_LINES \
    "BUILD_SHA256=" DUCOM_FW_BUILD_SHA "\n" \
    "FIRMWARE_CODE=" DUCOM_FW_CODE "\n" \
    "BT_NAME=" DUCOM_FW_BT_NAME "\n"
#else
#define DUCOM_IDENTITY_LINES ""
#endif
```

然后在原 sys_build_info 的字符串拼接中插入 DUCOM_IDENTITY_LINES。
以下是位置示例，不是替换原 SDK 整个定义的代码：

```c
/* 保留原来的段属性、used 属性、宏、CHIP/KERNEL/日期/CRC 与结束符。 */
const char sys_build_info[] =
    /* 原 SDK 的其他内容 */
    "\nCHIP=" CHIP_NAME "\n"
    "KERNEL=" KERNEL_NAME "\n"
    "BUILD_DATE=" __DATE__ " " __TIME__ "\n"
    DUCOM_IDENTITY_LINES
    /* 原 SDK 的其他内容 */
    "CRC32_OF_IMAGE=0x00000000\n";
```

CHIP_NAME/KERNEL_NAME 是说明用占位宏，必须保留你 SDK 原来使用的宏，不能照抄覆盖。
sys_build_info 应仍为单一 NUL 结尾的信息块；不要在扩展中写入提前截断的 NUL。
不要添加 FW_ID_SCHEMA=1：这个旧完整格式还要求 SOURCE_SHA256、IMAGE_SHA256、
IDENTITY_TARGET 和对应的全镜像 SHA 链，本最小版本没有宣称实现那套格式。
不要修改原 BUILD_DATE 的格式，不设置全局 SOURCE_DATE_EPOCH/TZ 来“稳定校验码”。

插入位置的实测结论：多数 BES SDK 的 CRC32_OF_IMAGE 不在块尾而在块中部，
其后还有 HW_VERSION、BUILD_DATE、REV_INFO、SW_VERSION 等字段。此时应把
DUCOM_IDENTITY_LINES 追加在信息块末尾（最后一个字段之后、结束换行之前），
使原有字段偏移完全不变；若照上方示例插在 CRC 之前，SW_VERSION 等字段会被
推出块首 512 字节读取窗口。实测（best1307p）：追加 173 字节后 CRC 关键字距
文件尾从约 120B 变为约 286B，仍在设备端尾部窗口内（见第 8 节）。

若 SDK 可能自带 BT_NAME 字段（如 BESUI 构建传 BTLE_NAME_FIXED_EN=1 时输出
"BT_NAME=" BESUI_BT_NAME），需用宏守卫让 DUCOM 的 BT_NAME 行仅在 SDK 未输出时
出现，保证字段唯一：

```c
#if defined(DUCOM_FW_IDENTITY) && !defined(BTLE_NAME_FIXED_EN)
    NEW_LINE_STR "BT_NAME=" DUCOM_FW_BT_NAME
#endif
```

注意 BES Makefile 惯例只有变量值为 1 时才追加 -D（ifeq ($(VAR),1) 模式），
传 VAR=0 不定义宏，因此源码里 #if defined(X) 为真即代表 X=1；
build_info.c 中原有的 defined() 判断都遵循这一语义，守卫宏要保持一致。

## 8. 必须检查首尾 512 字节与只读容量

新增字段长度固定为 173 字节（BUILD_SHA256 78 + FIRMWARE_CODE 23 + BT_NAME 72）。
这是本例的固定容量，不是所有 BES SDK 均可无条件容纳的大小。

查看你的 SDK TOTA/OTA 查询代码：
- 如果从 sys_build_info 开头扫描 512 字节，原 CHIP/BUILD_DATE/CRC 等必需字段必须仍完整可见。
- 如果从镜像末尾读取 512 字节搜索 CRC，CRC32_OF_IMAGE 及 8 个数字必须仍在范围内。
- 原链接段、尾部加载地址、分区和 Flash 预算必须保持有效。

实测（best1307p）的尾部消费者语义，供对照你的 SDK：
- services/ota/cmse/tz_ota.c 的 cmse_ota_check_image_data_sanity_crc 读取窗口为
  镜像末尾 512~1023 字节（起点按 (total-512)/512*512 向下对齐），关键字及 8 位
  数字必须完整落在窗口内；插入字段后应实际计算关键字距文件尾的距离确认，
  不要只看块内偏移。
- utils/lzma/bes_lzma_utils.c 的解压循环按 15 字节前缀（strncmp 长度 15，
  "CRC32_OF_IMAGE=" 不含后续 0x）判断容器结束；自写解析器按 16 字节比较会永远失配。
- 固件 .rodata 内存在同名关键字常量（image_info_sanity_crc_key_word =
  "CRC32_OF_IMAGE=0x"，tz_ota.c 与 bes_lzma_utils.c 各一份），全文件搜索该关键字
  会得到 2~3 处；任何定位与校验逻辑必须先定位 "\nCHIP=" 信息块、再在块内查找，
  不能按全文件唯一性查找。

建议用以下脚本检查未压缩 BIN（保存为 check_layout.py）：

```python
import re
import sys
from pathlib import Path

data = Path(sys.argv[1]).read_bytes()
blocks = list(re.finditer(rb"\nCHIP=[^\x00]{1,8192}\x00", data))
if len(blocks) != 1:
    raise SystemExit("build_info 不是唯一有效信息块")
block = blocks[0]
for field in [b"CHIP=", b"KERNEL=", b"BUILD_DATE=", b"CRC32_OF_IMAGE=0x"]:
    offset = block.group().find(field)
    end = block.group().find(b"\n", offset)
    if offset < 0 or end < 0 or end >= 512:
        raise SystemExit("原查询字段超出首512字节: " + repr(field))
crc = re.search(rb"\nCRC32_OF_IMAGE=0x[0-9A-Fa-f]{8}\n", block.group())
if crc is None or block.start() + crc.start() + 1 < len(data) - 512:
    raise SystemExit("CRC不在镜像尾512字节")
print("build_info/CRC读取窗口检查通过；仍需检查ELF/map及设备")
```

如果原 SDK 本身不采用这些窗口，应以实际消费者和打包器为准调整检查；
不要为了通过检查盲目扩大设备端扫描范围。插入位置容纳不了时，先缩减可选 BT_NAME，
同步固定容量预算，再重新评估；不要移动尾部地址结构或 NV/ANC/factory 分区。

## 9. 正式构建流程

```sh
# 在 SDK 根目录执行；参数要和 policy.json 的有效配置一致。
python3 tools/fw_identity/prepare.py --root . --policy tools/fw_identity/policy.json

# 首次接入、参数变更、依赖可靠性未确认时，使用项目真实的 clean + 全量构建。
# 以下 make 命令只是结构示意，替换为产品原命令及启用宏/include 的既有接口。
make T=your_product_main clean
make T=your_product_main DEBUG=0 BLE=1 -j8

# 压缩前：填充镜像内层 CRC（范围=镜像开头至数字之前，见 12.1）
python3 tools/fw_identity/fill_crc.py out/your_product_main/your_product_main.bin

# 保留 SDK 原地址填充 → CRC → 组合/压缩 → 签名流程；本例不做链接后patch。
# 压缩后：填充容器尾 CRC（范围=容器开头至尾行数字之前）
python3 tools/fw_identity/fill_crc.py out/your_product_main/your_product_main_compressed.bin

python3 tools/fw_identity/prepare.py --root . --policy tools/fw_identity/policy.json --verify
python3 tools/fw_identity/check_layout.py out/your_product_main/your_product_main.bin
python3 tools/fw_identity/verify_image.py out/your_product_main/your_product_main.bin generated/fw_identity.json
```

必须让 C 依赖追踪认识生成头文件；改变它后 build_info.o 要正确重编译。
首次可检查编译日志和 .d 依赖文件。只更新头文件但没有重新编译 build_info.o 会造成旧码。
发布入口应复核 BIN 中的 BUILD_SHA256 和 FIRMWARE_CODE 与 generated/fw_identity.json 一致。
下面脚本保存为 verify_image.py，放入 inputs 扫描范围：

```python
import binascii
import json
import re
import sys
from pathlib import Path

data = Path(sys.argv[1]).read_bytes()
report = json.loads(Path(sys.argv[2]).read_text(encoding="utf-8"))
blocks = list(re.finditer(rb"\nCHIP=[^\x00]{1,8192}\x00", data))
if len(blocks) != 1:
    raise SystemExit("信息块数量不正确")
block = blocks[0].group()
def value(key):
    found = re.findall(rb"(?:^|\n)" + key + rb"=([^\r\n\x00]*)", block)
    if len(found) != 1:
        raise SystemExit("字段缺失或重复: " + repr(key))
    return found[0].decode("ascii")
build = value(b"BUILD_SHA256")
code = value(b"FIRMWARE_CODE")
if (build != report["build_sha256"] or code != report["firmware_code"]
        or code != f"{binascii.crc32(bytes.fromhex(build)) & 0xFFFFFFFF:08X}"):
    raise SystemExit("BIN与本轮输入身份不匹配，拒绝交付")
print("本轮产物身份一致:", code)
```

```sh
python3 tools/fw_identity/verify_image.py \
  out/your_product_main/your_product_main.bin generated/fw_identity.json
```

复扫输入只能发现输入变化，不能单独证明所有 .o 来自本轮源码；
需要 clean 与增量构建对照，覆盖保留 mtime 的改动、有效参数变更、库替换和资源新增。
构建中输入发生变化时停止发布，不能把上一轮 BIN 配成本轮新清单。
归档完整输入清单、工具链、有效选项、ELF/map、最终 BIN/OTA 和各自 SHA-256。

fill_crc 类填值工具的定位规则（实测踩坑，缺一不可）：
- 镜像内定位必须先找 "\nCHIP=" 信息块、再在块内找关键字（第 8 节 rodata 常量坑），
  不能按全文件唯一性查找；
- 判断压缩容器不能依赖文件头：实测未压缩 BIN 前 4 字节也可能是 FF FF FF FF，
  应按尾部 26 字节 "CRC32_OF_IMAGE=0x[8位十六进制]\n" 格式判别；
- 填充幂等：CRC 数字本身在覆盖范围之外，重复执行结果不变。

verify_ota 类压缩 OTA 校验应做三件事：容器结构解析（头、逐块长度、尾行格式）、
逐块 LZMA 解压后与未压缩 BIN 逐字节比对（直接证明"解压后版本码与 BIN 相同"，
同时证明压缩流无损坏、重组无错位）、内外两层 CRC 分别重算比对。
构建脚本串接时注意：./build.sh | tail 这类管道会用 tail 的退出码掩盖构建失败，
自动化应把输出重定向到日志文件后显式传递脚本退出码。

## 10. 在 DuCom 中验证

1. 进入“插件管理”，启用“校验码查询工具”，点击“打开功能”，或从工具菜单打开。
2. 选择最终未压缩 BIN：应识别恒玄，显示 best 系列芯片、默认蓝牙名、原 BUILD_DATE。
3. 版本码必须与 generated/fw_identity.json 中 firmware_code 相同。
4. 再查询 SDK 生成的受支持分块 LZMA OTA：解压后的版本码应与 BIN 相同。
5. 如果只嵌入本例的最小字段，状态为“已读取校验码；未完成整文件完整性验证”，这是预期。
6. 原厂 CRC 不一致时应显示校验不一致；不要删除 CRC 行或取消校验来绕过错误。
7. 压缩 OTA 显示“校验不一致”时，多数是容器尾 CRC 或镜像内层 CRC 仍是占位
   0x00000000（填值工具缺失是常见根因）：插件会分别重算两层 CRC，占位值必然
   不匹配。正确修复是按 12.1 的范围真实填充两层 CRC，而不是删除 CRC 行绕过。

本例不生成 IMAGE_SHA256，不提供来源签名。原厂 CRC 的范围以 SDK 定义为准。
加密或签名容器需要对应的合法处理流程，不要修改已签名镜像后继续交付。

## 11. 验收与排障

连续两次相同输入：FIRMWARE_CODE/BUILD_SHA256 相同；真实时间是否更新取决于 SDK 的重编译机制。
修改注释、一个字符、资源、有效编译参数或预编译库：应改变身份；恢复输入应恢复原码。
删除 out/generated 后：正式构建入口应自动重建生成文件。
停用 DUCOM_FW_IDENTITY：应恢复原 build_info 路径。
map：只读容量增加在预算内，RAM/线程/NV/初始化逻辑无额外变化。
最后独立验证冷启动、烧录、真实 OTA、升级后首启、NV/校准保持及产测/TOTA 原行为。

常见问题：
- 显示未知厂商：选择了辅助镜像/ELF/HEX、加密容器，或缺少有效 CHIP/KERNEL/BUILD_DATE 信息块。
- 显示未嵌入：宏未启用、生成头文件未被引用、build_info.o 没重编译或选择了旧固件。
- 版本码与构建标识不匹配：字段被单独改动、使用旧生成头文件或填入了 OTA CRC。
- 原 CRC 不匹配：核对原 CRC owner、地址填充和打包顺序；不要在最终 CRC/签名后修改内容。
- 压缩 OTA 校验不一致：两层 CRC32_OF_IMAGE 均可能为占位 0x00000000，填值脚本缺失
  是常见根因；按 12.1 的范围真实填充，占位值被插件判为不一致是正确行为。
- 全文件多处 CRC32_OF_IMAGE：.rodata 关键字常量与信息块字段同名，定位必须在
  CHIP 信息块内进行（见第 8 节）。
- 接入调试期间每次构建版本码都变：工具脚本与策略本身也是身份输入，改它们变码
  是预期；接入稳定后相同输入同码，交付文件名的短码随之更新。
- BT_NAME 未输出或重复：BES Makefile 只有变量=1 才定义宏；SDK 自带固定 BT_NAME
  （如 BTLE_NAME_FIXED_EN=1）时需用宏守卫避免 DUCOM 重复输出（见第 7 节）。
- 蓝牙名不同：本工具读取固件默认名称，不读取耳机 NV 或 APP 修改后的实时名称。
- 杰理/蓝讯时间附“文件修改时间”：当前受支持格式没有可靠产品编译时间，因此明确使用文件时间兜底。

## 12. 工程集成注意事项

以下要求适用于 BES 固件构建集成，不依赖特定产品、芯片配置或工程目录。

### 12.1 压缩 OTA 的内外层 CRC 必须分别解释

分块 LZMA OTA 至少包含两个不同的字节域：解压后的固件镜像和压缩容器。
两者均可能使用名为 CRC32_OF_IMAGE 的字段，但不能仅凭字段同名就要求数值相等。

| 校验项 | 常见覆盖范围 | 验证依据 |
| --- | --- | --- |
| 镜像内部 CRC | 未压缩镜像开头至内部 CRC 数字之前 | SDK 镜像生成器、升级拷贝代码 |
| 压缩包尾 CRC | 压缩容器开头至尾部 CRC 数字之前 | SDK OTA 接收及容器打包代码 |
| 旧式压缩包尾码 | 部分打包器直接复制镜像内部 CRC | 已确认的旧格式及内层实际 CRC 校验 |

应按照实际 SDK 确认覆盖起点、终点、初值、最终异或、magic 处理及数字编码。
压缩包 CRC 与镜像 CRC 不同不代表损坏；应分别重算。兼容旧式复制尾码时，
不能只比较两个文本字段，还应确认解压后镜像的实际 CRC 有效。
正确解压只能证明压缩流可解码，不能代替容器 CRC；修改 LZMA 属性后仍可解压的包也可能已损坏。
解析器更新前后，应同时验证当前格式、旧格式、错误尾码、截断、块长度越界和可解压容器篡改。
不要为满足查看工具而取消设备端校验、改用版本短码或改写已签名容器。

实测补充（best1307p SDK）：
- SDK 的常态可能是两层都只写占位 0x00000000：build_compressed_ota.sh 用 printf
  写占位尾行，镜像内层填值脚本 generate_crc32_of_image.py 缺失。此时压缩 OTA 在
  本插件显示“校验不一致”（容器 CRC 不匹配），设备端 tz_ota 的尾部窗口校验若执行
  也会失败。正确修复是把填值工具补回构建链：内层在压缩前、容器尾在压缩后填充，
  覆盖范围与上表一致（标准 CRC-32/zlib 变体，8 位大写十六进制数字，幂等可重复执行）。
- 设备端 bes_lzma_utils 的解压循环把容器尾行字节纳入接收 CRC 累计，接收端对收到
  的字节重算，因此填充真值不改变设备端行为。
- 两层 CRC 数值互不相等是正常的；工具与人都不要拿两个文本字段互相比较，
  应各自按自己的范围重算。

### 12.2 生成头文件与 Makefile 赋值顺序

产品配置中追加编译选项后，顶层 Makefile 的后续“:=”赋值可能覆盖先前结果。
因此必须检查 build_info.o 的真实编译命令和 .d 依赖，而不是只确认配置文件写了宏。
建议使用产品专用变量传递选项，在原 BUILD_INFO_FLAGS 最终组装位置引用；
避免把身份宏和 include 路径无条件传播到 PROGRAMMER、ROM、CP 等辅助目标。
生成头内容未变化时保持 mtime；内容变化时必须触发 build_info.o 重编译。

### 12.3 字段布局、换行和容量

扩展后的信息块允许大于 512 字节，但实际消费者需要的字段必须留在各自读取窗口内。
应分别检查“从信息块开头扫描”和“从镜像末尾扫描”，不能只检查字符串总长度。
必要时在确认消费者后调整字段顺序；若 SDK CRC 覆盖数字之前的前缀，
希望受该 CRC 保护的身份字段必须位于其前面，并按新位置重新计算原厂 CRC。
每个字段只出现一次，整个信息块只保留一个最终 NUL，原 BUILD_DATE 格式保持不变。
LF/CRLF 会影响长度和偏移；第 8 节的示例检查按 LF 编写，实际工具应兼容“\r?\n”。
固定 173 字节仅适用于第 7 节 LF 扩展，Flash 净增加还可能包含链接对齐。
禁止盲目扩大设备端扫描范围、移动 NV/ANC/factory 分区或向镜像尾部追加身份字节。

### 12.4 日常增量与正式发布

日常开发可保留 SDK 增量编译机制，每次更新输入身份并校验最终产物；
不要仅因注释或普通源码变化就强制清理全部对象文件，否则会丧失增量编译优势。
构建参数、配置、链接规则或工具链变化，以及上轮失败、对象来源不明时，应重新全量构建。
正式发布应完整采集工具链内容、clean 全量编译、复核输入、校验产物并归档清单。
开发/发布模式、并发数、时间或本机绝对路径不应仅因流程不同而改变主固件版本身份。

开发模式可缓存工具链摘要，缓存键应包含完整文件列表、大小、mtime/ctime及工具实际路径；
缓存元数据不能计入可移植身份。正式发布应绕过缓存重新计算内容摘要。
只记录 GCC 第一行版本不足以描述 cc1/cc1plus、链接器插件、specs、运行库等实际输入。
源码或库若被保留旧 mtime 替换，输入复扫可以发现内容变化，Make却可能复用旧对象；
因此“新短码已生成”不证明所有对象都来自新源码，出货必须使用全量发布入口。
构建成功状态只能在全部校验及必要辅助镜像完成后保存；失败不得留下可复用成功标记。

### 12.5 generated 目录与默认名称

generated 可作为可删除的自动生成目录，但统一入口必须先重建工具链清单和头文件再编译。
不要在清空生成目录后直接调用依赖其头文件的裸 make，也不要将输出反向计入输入摘要。
建议同时归档完整输入清单、有序有效参数、工具链摘要、ELF/map、最终文件 SHA-256。
若名称来自独立 factory 烧录区，或源码后备名含主机名/用户名，不能据此宣称一个固定产品默认名。
BT_NAME 应来自已确认的受控默认配置；未知时留空或显示未识别，不套用其它产品的名称。
固定容量名称可用空格填充，显示时去除填充；设备 NV/APP 动态改名不属于静态文件信息。

版本库约定（SVN 与 .gitignore 同理）：generated 与 out 用 svn:ignore 忽略，
属性修改本身要提交后才对团队生效；toolchain.txt 建议入库（见第 6 节）；
policy.json 与构建入口脚本必须同一次提交，否则他人构建出的版本码
会与实际生效参数不一致。

### 12.6 交付文件命名和历史文件清理

可采用以下名称，末尾短码取本轮已验证的 FIRMWARE_CODE，而不是 OTA CRC：

```text
product_main_12AB34CD.bin
product_main_compressed_12AB34CD.bin
```

仅修改文件名或复制文件，不修改固件字节；文件名是核对提示，解析仍应以内部字段为准。
在 CRC、压缩、签名及全部验证结束后再发布副本，核对副本 SHA-256 与原产物相同。
构建系统若依赖固定名 BIN，可保留固定名中间产物，并提供带短码的交付副本。
先成功生成本轮副本，再按严格命名规则清理同一 target、同一构建变体的历史短码文件；
不要使用目录级“*.bin”删除，也不要跨 target、跨 nores/硬件变体、跨日志或归档目录清理。
短码文件集合应有界，不随成功构建次数增长；保留交付清单记录本轮文件名及 SHA-256。

实测清理命令（bash，本轮两个文件名除外；-print 让清理动作在日志可见）：

```sh
find out/<target> -maxdepth 1 -type f -regextype posix-extended \
    -regex ".*/<target>(_compressed)?_[0-9A-F]{8}\.bin" \
    ! -name "<target>_<本轮短码>.bin" \
    ! -name "<target>_compressed_<本轮短码>.bin" \
    -print -delete
```

交付清单可写 out/<target>/fw_delivery.txt（target、本轮 firmware_code、两个副本的
SHA-256）。接入时应预埋几个伪造历史短码文件与无关命名文件实测清理逻辑，
确认旧短码被删、固定名产物与其他命名文件不被误删。

### 12.7 联合验收

- 不能仅用工程自带脚本验收，还应以实际交付版本的 DuCom 插件查询真实 BIN 和压缩 OTA。
- 两者 FIRMWARE_CODE/BUILD_SHA256 及解压负载一致；各自完整性字段按各自范围通过。
- 相同输入重复构建同码；源码、注释、资源、库、配置或工具链变化后变码，恢复输入恢复原码。
- 错误字段、重复信息块、错误长度、截断及篡改应拒绝；不得通过删除 CRC 字段绕过失败。
- 最小字段格式没有 IMAGE_SHA256，因此读取版本码、前缀 CRC 通过不等于整文件完整性或来源认证。
- PC 解析成功之后，仍须验证实际烧录、冷启动、真实 OTA、升级首启、NV/校准保持及原产测行为。

## 13. 第三方依赖

固件解析复用本地 FirmwareInspector 的 C# 算法；杰理算法不运行或分发 ufw_maker。
SharpCompress 0.48.1 用于 BES LZMA 解压，MIT License：
https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt
