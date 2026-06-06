namespace GpsTcpProxy.Protocol;

/// <summary>
/// Справочник типов пакетов JT/T808.
/// </summary>
public static class Jt808PacketTypes
{
    public sealed record PacketTypeInfo(ushort Id, string Code, string Name, string Direction);

    private static readonly Dictionary<ushort, PacketTypeInfo> Known = new()
    {
        // Терминал → платформа
        [0x0001] = new(0x0001, "0x0001", "ответ терминала", "terminal"),
        [0x0002] = new(0x0002, "0x0002", "пульс", "terminal"),
        [0x0003] = new(0x0003, "0x0003", "выход", "terminal"),
        [0x0100] = new(0x0100, "0x0100", "регистрация", "terminal"),
        [0x0102] = new(0x0102, "0x0102", "аутентификация", "terminal"),
        [0x0104] = new(0x0104, "0x0104", "сверка часов", "terminal"),
        [0x0107] = new(0x0107, "0x0107", "ответ на запрос свойств", "terminal"),
        [0x0108] = new(0x0108, "0x0108", "обновление тревоги", "terminal"),
        [0x0200] = new(0x0200, "0x0200", "телеметрия", "terminal"),
        [0x0201] = new(0x0201, "0x0201", "доп. телеметрия", "terminal"),
        [0x0301] = new(0x0301, "0x0301", "событие", "terminal"),
        [0x0302] = new(0x0302, "0x0302", "вопрос", "terminal"),
        [0x0303] = new(0x0303, "0x0303", "информационный сервис", "terminal"),
        [0x0500] = new(0x0500, "0x0500", "управление автомобилем", "terminal"),
        [0x0700] = new(0x0700, "0x0700", "данные eWaybill", "terminal"),
        [0x0701] = new(0x0701, "0x0701", "данные водителя", "terminal"),
        [0x0702] = new(0x0702, "0x0702", "идентификация водителя", "terminal"),
        [0x0704] = new(0x0704, "0x0704", "пакетная телеметрия", "terminal"),
        [0x0705] = new(0x0705, "0x0705", "CAN-данные", "terminal"),
        [0x0800] = new(0x0800, "0x0800", "мультимедиа", "terminal"),
        [0x0801] = new(0x0801, "0x0801", "загрузка мультимедиа", "terminal"),
        [0x0805] = new(0x0805, "0x0805", "поиск камеры", "terminal"),
        [0x0900] = new(0x0900, "0x0900", "данные GZIP", "terminal"),
        [0x0901] = new(0x0901, "0x0901", "GZIP-сжатие", "terminal"),

        // Платформа → терминал
        [0x8001] = new(0x8001, "0x8001", "общий ответ", "platform"),
        [0x8100] = new(0x8100, "0x8100", "ответ на регистрацию", "platform"),
        [0x8103] = new(0x8103, "0x8103", "установка параметров", "platform"),
        [0x8104] = new(0x8104, "0x8104", "запрос свойств", "platform"),
        [0x8105] = new(0x8105, "0x8105", "управление терминалом", "platform"),
        [0x8106] = new(0x8106, "0x8106", "запрос свойств", "platform"),
        [0x8107] = new(0x8107, "0x8107", "запрос свойств", "platform"),
        [0x8108] = new(0x8108, "0x8108", "запрос тревоги", "platform"),
        [0x8201] = new(0x8201, "0x8201", "запрос местоположения", "platform"),
        [0x8202] = new(0x8202, "0x8202", "отслеживание", "platform"),
        [0x8203] = new(0x8203, "0x8203", "отмена отслеживания", "platform"),
        [0x8300] = new(0x8300, "0x8300", "текстовое сообщение", "platform"),
        [0x8301] = new(0x8301, "0x8301", "событие", "platform"),
        [0x8302] = new(0x8302, "0x8302", "вопрос", "platform"),
        [0x8303] = new(0x8303, "0x8303", "информационный сервис", "platform"),
        [0x8304] = new(0x8304, "0x8304", "информационный сервис", "platform"),
        [0x8400] = new(0x8400, "0x8400", "телефонная книга", "platform"),
        [0x8401] = new(0x8401, "0x8401", "звонок", "platform"),
        [0x8500] = new(0x8500, "0x8500", "управление автомобилем", "platform"),
        [0x8600] = new(0x8600, "0x8600", "круговая зона", "platform"),
        [0x8601] = new(0x8601, "0x8601", "удаление зоны", "platform"),
        [0x8602] = new(0x8602, "0x8602", "прямоугольная зона", "platform"),
        [0x8603] = new(0x8603, "0x8603", "удаление зоны", "platform"),
        [0x8604] = new(0x8604, "0x8604", "полигональная зона", "platform"),
        [0x8605] = new(0x8605, "0x8605", "удаление зоны", "platform"),
        [0x8606] = new(0x8606, "0x8606", "маршрут", "platform"),
        [0x8607] = new(0x8607, "0x8607", "удаление маршрута", "platform"),
        [0x8700] = new(0x8700, "0x8700", "запись", "platform"),
        [0x8701] = new(0x8701, "0x8701", "запрос записи", "platform"),
        [0x8702] = new(0x8702, "0x8702", "управление записью", "platform"),
        [0x8800] = new(0x8800, "0x8800", "мультимедиа", "platform"),
        [0x8801] = new(0x8801, "0x8801", "запрос мультимедиа", "platform"),
        [0x8802] = new(0x8802, "0x8802", "хранилище мультимедиа", "platform"),
        [0x8803] = new(0x8803, "0x8803", "загрузка мультимедиа", "platform"),
        [0x8804] = new(0x8804, "0x8804", "запись", "platform"),
        [0x8805] = new(0x8805, "0x8805", "одиночная запись", "platform"),
        [0x8900] = new(0x8900, "0x8900", "данные GZIP", "platform"),
    };

    public static string GetName(ushort messageId) =>
        Known.TryGetValue(messageId, out var info) ? info.Name : "неизвестный";

    public static string GetCode(ushort messageId) =>
        Known.TryGetValue(messageId, out var info) ? info.Code : $"0x{messageId:X4}";

    public static bool IsFromTerminal(ushort messageId) =>
        !Known.TryGetValue(messageId, out var info) || info.Direction == "terminal";

    public static IReadOnlyCollection<PacketTypeInfo> GetAll() => Known.Values;
}
