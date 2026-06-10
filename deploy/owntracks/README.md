# OwnTracks — конфигурация для GpsTcpProxy

Файлы `config.otrc` импортируются в приложение OwnTracks:

**Settings → Connection → Mode → Private → Import settings**

## Файлы

| Файл | Назначение |
|------|------------|
| `config.otrc.example` | Шаблон с плейсхолдерами |
| `config.otrc.fedor` | Готовый конфиг для пользователя **fedor** (`phone-fedor`) |

## Что нужно подставить (шаблон)

| Поле | Описание |
|------|----------|
| `host` | IP или домен сервера с MQTT (TLS, порт 8883) |
| `username` | MQTT-логин (`Mqtt.Username` в `appsettings.json` сервера) |
| `password` | MQTT-пароль (`Mqtt.Password` в `appsettings.json` сервера) |
| `deviceId` | ID устройства — **должен совпадать** с ключом в `devices.json` |
| `clientId` | Уникальный MQTT client ID для этого телефона |
| `tid` | Короткая метка трекера (2 символа), отображается в OwnTracks |

## MQTT-топики

При пустом `pubTopicBase` OwnTracks публикует в:

```
owntracks/{username}/{deviceId}
```

Команды (wakeup / reportLocation) приходят на:

```
owntracks/gpstcpproxy/phone-fedor/cmd
```

## Обязательные настройки для wakeup

- `cmd`: `true` — приём команд
- `allowRemoteLocation`: `true` — ответ на `reportLocation`
- `tls`: `true`, `port`: `8883` — если брокер с TLS (как в `deploy/mosquitto.conf`)

## Регистрация на сервере

Перед использованием добавьте устройство в `devices.json`:

```json
"phone-fedor": {
  "name": "📱 Fedor's phone",
  "protocol": "OwnTracks"
}
```

И при необходимости — в `appsettings.json`:

```json
"TheftDetection": {
  "PhoneDeviceIds": ["phone", "phone-fedor"]
}
```

После изменений перезапустите сервис: `sudo systemctl restart gpstcpproxy`.

## Проверка

На сервере после публикации с телефона в логах должно появиться:

```
[MQTT] ← owntracks/gpstcpproxy/phone-fedor (...)
[MQTT] location 📱 Fedor's phone: ...
```

Команда **wakeup** в Telegram отправит `reportLocation` на все телефоны из `PhoneDeviceIds`.
