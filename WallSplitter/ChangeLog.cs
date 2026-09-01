using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallSplitter
{
    // NAMER의 이름 변경, 재료 지정의 재료 지정/삭제/클래스·설명 변경 — 이 네 가지가 각각 무엇을 바꿨는지를
    // 하나의 스키마로 표현한다. ElementId는 문서마다 다르므로 절대 저장하지 않고, 항상 Key(이름)로 대상
    // 문서에서 "같은 사례"를 다시 찾는다(모델간 변경 반영이 이 로그를 읽어 다른 문서에 재현할 때 사용).
    // NamerWindow.NamerCategory(internal)를 그대로 참조하므로(아래 ChangeLogEntry.Category), 이 파일의
    // 타입들도 전부 internal로 맞춘다 - public이면 "public 속성이 internal 타입보다 접근하기 어려움"
    // 컴파일 오류(CS0053)가 난다. 이 어셈블리 밖에서 쓸 일도 없으므로 internal이 정확히 맞다.
    internal enum ChangeKind
    {
        Rename,             // NAMER: Category 카테고리에서 이름이 Key였던 요소를 NewValue로 변경
        MaterialAssign,     // 재료 지정: 유형 이름이 Key인 유형의 재료를 OldValue(이름)에서 NewValue(이름)로 변경
        MaterialDelete,     // 재료 지정: 이름이 Key인 재료를 삭제
        MaterialIdentityEdit, // 재료 지정: 이름이 Key인 재료의 Field(클래스/설명)를 NewValue로 변경
    }

    internal enum IdentityField
    {
        MaterialClass,
        Description,
    }

    internal class ChangeLogEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; }
        public string SourceDocumentTitle { get; set; } = "";

        public ChangeKind Kind { get; set; }

        // Rename 전용 - 어느 NAMER 카테고리였는지. 카테고리 목록이 늘어나도 여기가 따로 어긋나지 않도록
        // 별도 enum을 두지 않고 NamerWindow.NamerCategory를 그대로 참조한다.
        public NamerWindow.NamerCategory? Category { get; set; }
        // MaterialIdentityEdit 전용 - 클래스/설명 중 어느 필드였는지.
        public IdentityField? Field { get; set; }

        // 대상 문서에서 "같은 사례"를 찾을 때 쓰는 이름. Rename=원래 이름, MaterialAssign=유형 이름,
        // MaterialDelete/MaterialIdentityEdit=재료 이름.
        public string Key { get; set; } = "";
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }

        // MaterialAssign 전용 - 유형 하나가 슬롯(레이어/재료 파라미터)을 여러 개 가질 수 있게 되면서 추가됨.
        // MaterialSlot.Label을 그대로 담아, 대상 문서에서 유형 이름이 같아도 "그 중 어느 슬롯"이었는지를
        // ElementId 없이(문서마다 다르므로) 이름만으로 다시 찾을 수 있게 한다. 슬롯이 하나뿐인 유형은 ""로
        // 저장되며(MaterialSlotFinder.FindSlotByLabel 참고), 이 필드가 없던 과거 기록(null)도 같은 방식으로
        // "슬롯이 하나뿐이면 그것" 규칙으로 호환된다.
        public string? SlotLabel { get; set; }

        // Rename+Category=Material 전용 - 재료 이름이 대상 문서에서 이미 다른 재료가 쓰고 있을 때
        // 병합할지(true, NamerCommand.RenameMaterial과 동일 정책) 숫자를 붙일지(false)를 그대로 재현하기 위함.
        public bool? MergeDuplicateMaterials { get; set; }
    }

    // NAMER/재료 지정에서 "최종 적용"이 실제로 커밋된 뒤 자동으로 누적되는 변경 기록.
    // NamingSettings와 같은 방식으로 %APPDATA%\WallSplitter\change-log.json에 전역(프로젝트 무관) 저장된다.
    internal class ChangeLog
    {
        public List<ChangeLogEntry> Entries { get; set; } = new List<ChangeLogEntry>();

        private static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WallSplitter", "change-log.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static ChangeLog Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    ChangeLog? loaded = JsonSerializer.Deserialize<ChangeLog>(json, JsonOptions);
                    if (loaded != null)
                    {
                        loaded.Entries ??= new List<ChangeLogEntry>();
                        return loaded;
                    }
                }
            }
            catch
            {
                // 로그 파일이 손상된 경우 빈 로그로 대체 (NamingSettings.Load와 같은 방침)
            }
            return new ChangeLog();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }

        // 호출하는 쪽(NamerCommand/MaterialAssignCommand)이 Transaction.Commit()의 반환값으로 실제
        // 커밋 성공을 확인한 뒤에만 불러야 한다 - 롤백된 변경까지 기록되면, 실제로는 모델에 반영되지 않은
        // "거짓 기록"을 다른 모델에 재현하려 시도하게 된다.
        public static void Append(IEnumerable<ChangeLogEntry> entries)
        {
            List<ChangeLogEntry> list = entries as List<ChangeLogEntry> ?? entries.ToList();
            if (list.Count == 0) return;

            ChangeLog log = Load();
            log.Entries.AddRange(list);
            // 이 시점엔 모델 변경이 이미 커밋된 뒤다. %APPDATA% 쓰기 실패(권한/디스크)로 커맨드 전체를
            // 실패로 보고하면, 실제로는 잘 반영된 작업이 실패한 것처럼 보인다. 기록만 조용히 포기한다.
            try { log.Save(); }
            catch { /* 변경 기록은 부가 기능이므로 저장 실패가 본 작업을 실패시키지 않는다. */ }
        }
    }
}
