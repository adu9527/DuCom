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
5. 保存原始构建命令、产物及 map，用独立开发分支接入。

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

## 8. 必须检查首尾 512 字节与只读容量

新增字段长度固定为 173 字节（BUILD_SHA256 78 + FIRMWARE_CODE 23 + BT_NAME 72）。
这是本例的固定容量，不是所有 BES SDK 均可无条件容纳的大小。

查看你的 SDK TOTA/OTA 查询代码：
- 如果从 sys_build_info 开头扫描 512 字节，原 CHIP/BUILD_DATE/CRC 等必需字段必须仍完整可见。
- 如果从镜像末尾读取 512 字节搜索 CRC，CRC32_OF_IMAGE 及 8 个数字必须仍在范围内。
- 原链接段、尾部加载地址、分区和 Flash 预算必须保持有效。

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

# 保留 SDK 原地址填充 → CRC → 组合/压缩 → 签名流程；本例不做链接后patch。
python3 tools/fw_identity/prepare.py --root . --policy tools/fw_identity/policy.json --verify
python3 tools/fw_identity/check_layout.py out/your_product_main/your_product_main.bin
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

## 10. 在 DuCom 中验证

1. 进入“插件管理”，启用“校验码查询工具”，点击“打开功能”，或从工具菜单打开。
2. 选择最终未压缩 BIN：应识别恒玄，显示 best 系列芯片、默认蓝牙名、原 BUILD_DATE。
3. 版本码必须与 generated/fw_identity.json 中 firmware_code 相同。
4. 再查询 SDK 生成的受支持分块 LZMA OTA：解压后的版本码应与 BIN 相同。
5. 如果只嵌入本例的最小字段，状态为“已读取校验码；未完成整文件完整性验证”，这是预期。
6. 原厂 CRC 不一致时应显示校验不一致；不要删除 CRC 行或取消校验来绕过错误。

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
- 蓝牙名不同：本工具读取固件默认名称，不读取耳机 NV 或 APP 修改后的实时名称。
- 杰理/蓝讯时间附“文件修改时间”：当前受支持格式没有可靠产品编译时间，因此明确使用文件时间兜底。

## 12. 第三方依赖

固件解析复用本地 FirmwareInspector 的 C# 算法；杰理算法不运行或分发 ufw_maker。
SharpCompress 0.48.1 用于 BES LZMA 解压，MIT License：
https://github.com/adamhathcock/sharpcompress/blob/master/LICENSE.txt
