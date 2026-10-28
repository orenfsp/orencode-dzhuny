# Модель и API

`Store.cs` создаёт таблицы: accounts, people, organizations, events, applications,
results, qualifications, roster, links, assignments, consents, logs, notifications,
outbox, documents, groups, lessons, standards, assessments, prizeRules, petitions,
certificates, tokens, clients, codes, oidcTokens, ratingRules.
Каждая строка: id + JSON payload. Запись всех изменений команды — одна SQLite-транзакция.

Account → Person; Application/Result → Event + Person; Link → Coach + Athlete + Discipline;
Assignment → Event + Judge; Qualification → Person + Result/основание;
Group → Coach + Organization + Members; Lesson → Group; Assessment → Person + Standard + snapshot;
Certificate → Result; Petition → year + snapshot кандидатов и правил.

## Авторизация

`GET /api/auth/csrf` возвращает token. Для изменяющих cookie-запросов передавайте
заголовок `X-CSRF`. После входа и смены роли запросите свежий token.

- POST /api/auth/login — email, password
- POST /api/auth/register — email, password, name, birthDate, city, sex, discipline, consent;
  для несовершеннолетнего guardian и guardianConsent
- POST /api/auth/role — role, только назначенная сервером роль
- POST /api/auth/logout
- GET /api/auth/me
- GET /api/state?year=2026&discipline=0 — данные с учётом доступа

## Команды

`POST /api/commands/{name}` с JSON. Сервер создаёт идентификаторы и проверяет права.

| Команда | Основные поля |
| --- | --- |
| event-save | title, date, deadline, discipline, capacity, minAge, maxAge, city, venue, format, level, type, calendarPlan, calendarNumber, regulation, requiredDocuments, revision |
| event-status | id, status (open/closed) |
| coach-request | person, discipline |
| coach-confirm | id, accept |
| coach-status | id, status (active/paused/ended) |
| apply | event, person, consent, documentIds |
| application-documents | id, documentIds |
| application-review | id, status (approved/rejected), note |
| assign | event, judges: [{person,position}] |
| publish | event, scores: [{person,score}] |
| qualification | person, type (rank/category), value, resultId, basis |
| qualification-review | id, status, from, until, order, note |
| roster | person, discipline, year; повторный вызов переключает включение |
| group / lesson | см. Extras.cs |
| standard / assessment / promote | см. Extras.cs; сохраняется снимок проверяемого правила |
| prize-rule / petition / petition-status | см. Extras.cs |
| api-token | name, until, scopes |
| token-revoke | id |
| client | name, redirectUri |
| certificate | result |

Точные проверки и остальные параметры находятся в Commands.cs, Extras.cs и app.js.
Ошибки: 400 — данные/переход; 401 — вход; 403 — права; 404 — запись; 409 — конфликт.

## Документы и интеграции

- POST /api/documents — multipart: person, kind, file (PDF/PNG/JPEG/DOCX, до 10 МБ)
- GET /api/documents/{id} — проверка владельца/прав
- GET /api/export/{kind}?id=...&year=2026
  kind: protocol, history, regulation, judges, judge-history, roster, order, census, registry, prize
- GET /api/certificates/{id}/print — справка, QR, печать в PDF
- GET /verify/{token} — публичная проверка с сокращёнными персональными данными
- GET /api/integrations/events — scope events:read
- GET /api/integrations/results — scope results:read
- POST /api/integrations/results — scope results:write, тот же формат и проверки publish

Bearer-токен интеграции показывается один раз, хранится хеш, проверяются срок и отзыв.

## ФСП ID

Discovery: `/.well-known/openid-configuration`, ключи: `/.well-known/jwks.json`.
`/connect/authorize` → согласие → code → `/connect/token` → `/connect/userinfo`.
Требуются точный redirect_uri, PKCE S256, scope openid (дополнительно profile).
Код одноразовый, 2 минуты. Access token и подписанный RS256 ID token — 15 минут.
Клиент `/oidc-demo.html` проверяет state, nonce, подпись, issuer, audience и sub.
