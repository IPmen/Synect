# Проверка выбора инструментов (40 вопросов)

> **Гипотезы.** Вопросы составлены разработчиками как предположения о том, что спрашивают бухгалтеры, и **должны быть
> заменены реальными вопросами бухгалтеров** после демонстраций. Узбекские формулировки — черновик (см.
> [glossary.md](glossary.md)), требуют проверки носителями языка.
>
> Как проверяется: автоматический тест (`ToolSelectionEvalTests`) читает таблицу ниже и проверяет только, что каждый
> ожидаемый инструмент существует, а его описание содержит примеры вопросов на русском и на узбекском (латиница и
> кириллица). **Выбор инструмента моделью проверяется вручную на демонстрации, не в CI**: задайте вопрос в Claude с
> подключённым Synect (ключ аккаунта с несколькими организациями) и сравните вызванный инструмент и параметры с таблицей.
>
> Язык: ru — русский, uz-Latn — узбекская латиница, uz-Cyrl — узбекская кириллица, mix — смешанная формулировка.
> Параметр `organization` указан там, где в вопросе названа организация (ИНН из демо-данных `seed-account`).

| # | Вопрос | Язык | Ожидаемый инструмент | Ключевые параметры |
|---|---|---|---|---|
| 1 | Какие организации у нас подключены? | ru | `list_organizations` | — |
| 2 | Qaysi tashkilotlar ulangan? | uz-Latn | `list_organizations` | — |
| 3 | Что по всем клиентам? У кого проблемы? | ru | `get_portfolio_overview` | — |
| 4 | Barcha mijozlar bo'yicha holat qanday? | uz-Latn | `get_portfolio_overview` | — |
| 5 | Барча мижозлар бўйича ҳолат қандай? | uz-Cyrl | `get_portfolio_overview` | — |
| 6 | Покажи сводку по Ромашке и по 404802602 | ru | `get_portfolio_overview` | `organizations` = «Ромашка, 404802602» |
| 7 | Какие подключения к банкам не работают? | ru | `get_connections_overview` | `only_needing_attention` = true |
| 8 | Қайси уланишлар ишламаяпти? | uz-Cyrl | `get_connections_overview` | `only_needing_attention` = true |
| 9 | Где мы ждём доступ от банка (заявление подано)? | ru | `get_connections_overview` | — (смотреть `onboarding_status`) |
| 10 | Что требует внимания по всем клиентам? | ru | `get_attention_items` | `organization` = all |
| 11 | Nimalarga e'tibor berish kerak? (по 404802603) | mix | `get_attention_items` | `organization` = 404802603 |
| 12 | Есть что-то срочное по всем организациям? | ru | `get_attention_summary` | `organization` = all |
| 13 | Муҳим огоҳлантиришлар борми? | uz-Cyrl | `get_attention_summary` | `organization` (переспросить, если не названа) |
| 14 | Отметь это предупреждение как просмотренное | ru | `update_attention_item` | `item_id`, `action` = seen |
| 15 | Сколько денег на счетах у «Demo accounting org 01»? | ru | `get_account_balances` | `organization` = название |
| 16 | 404802601 hisob raqamlaridagi qoldiq qancha? | uz-Latn | `get_account_balances` | `organization` = 404802601 |
| 17 | Ҳисоб рақамлардаги қолдиқ қанча? | uz-Cyrl | `get_account_balances` | `organization` (переспросить) |
| 18 | Сколько пришло и ушло в октябре у 404802605? | ru | `get_cash_flow` | `organization`, `from`, `to` |
| 19 | Oktabrda tushum va chiqim qancha? | uz-Latn | `get_cash_flow` | `from`, `to`, `organization` |
| 20 | Самые крупные платежи за месяц | ru | `get_largest_transactions` | `from`, `to`, `organization` |
| 21 | Кеча қандай тўловлар бўлди? | uz-Cyrl | `get_transactions_for_day` | `date`, `organization` |
| 22 | Сколько мы заплатили Ромашке в этом году? | ru | `find_counterparties`, затем `get_counterparty_payments` | `query` = Ромашка; `inn` из ответа |
| 23 | Romashka MChJga qancha to'ladik? | uz-Latn | `find_counterparties`, затем `get_counterparty_payments` | `query`, `inn` |
| 24 | Кто наши крупнейшие клиенты за год? | ru | `get_top_counterparties` | `from`, `to`, `direction` |
| 25 | Энг катта мижозларимиз кимлар? | uz-Cyrl | `get_top_counterparties` | `from`, `to` |
| 26 | Какие ЭСФ ждут нашей подписи? | ru | `get_documents_awaiting_our_signature` | `organization` |
| 27 | Qaysi hujjatlar imzomizni kutmoqda? | uz-Latn | `get_documents_awaiting_our_signature` | `organization` |
| 28 | Рад этилган ҳисоб-фактуралар борми? | uz-Cyrl | `get_rejected_documents` | `from`, `to` |
| 29 | Найди счёт-фактуру № 15 | ru | `search_documents` | `number` = 15 |
| 30 | 7-sonli shartnoma bo'yicha hujjatlar | uz-Latn | `search_documents` | `contract_number` = 7 |
| 31 | НДС за октябрь по 404802601 | ru | `get_vat_summary` | `organization`, `from`, `to` |
| 32 | Oktabr uchun QQS qancha? | uz-Latn | `get_vat_summary` | `from`, `to` |
| 33 | Какие акты и ЭСФ не оплачены? | ru | `get_unpaid_documents` | `from`, `to` |
| 34 | Тўланмаган ҳисоб-фактуралар | uz-Cyrl | `get_unpaid_documents` | `from`, `to` |
| 35 | Какие поступления не сопоставлены с документами? | ru | `get_unmatched_payments` | `direction` = incoming |
| 36 | Debitorlik qarzi qancha? | uz-Latn | `get_counterparty_balance_estimate` | `organization` |
| 37 | Сколько нам должна Ромашка (дебиторка)? | ru | `find_counterparties`, затем `get_counterparty_balance_estimate` | `inn` из поиска |
| 38 | Насколько свежие данные у 404802610? | ru | `get_data_freshness` | `organization` = 404802610 |
| 39 | Bank ma'lumotlarini yangila | uz-Latn | `request_data_refresh` | `source` = bank, `organization` |
| 40 | Покажи qoldiq по всем счетам Ромашки | mix | `get_account_balances` | `organization` = Ромашка (при нескольких — переспросить) |
