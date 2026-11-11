# Глоссарий бухгалтерских терминов (русский, узбекский)

> **Черновик.** Список составлен разработчиками как рабочий черновик для описаний инструментов MCP и примеров вопросов.
> Узбекские варианты (латиница и кириллица) **требуют проверки носителями языка и практикующими бухгалтерами**:
> возможны неточности, региональные и профессиональные варианты. Эти термины используются в описаниях инструментов
> (`src/Synect.Mcp/Tools/ToolExamples.cs`) и в `docs/tool-selection-eval.md`.

| # | Русский | Узбекский (латиница) | Узбекский (кириллица) | Где в Synect |
|---|---|---|---|---|
| 1 | Счёт-фактура, ЭСФ (электронный счёт-фактура) | hisob-faktura, elektron hisob-faktura (EHF) | ҳисоб-фактура, электрон ҳисоб-фактура | документы Didox, сверка |
| 2 | Акт (выполненных работ, оказанных услуг) | dalolatnoma (bajarilgan ishlar dalolatnomasi) | далолатнома | документы, `get_signed_unpaid_acts` |
| 3 | Договор | shartnoma | шартнома | номер договора в назначении платежа |
| 4 | Накладная (товарная) | yuk xati, tovar-transport yuk xati | юк хати | документы Didox |
| 5 | Доверенность | ishonchnoma | ишончнома | документы Didox (не сверяются) |
| 6 | Акт сверки | solishtirma dalolatnoma, hisob-kitoblarni solishtirish dalolatnomasi | солиштирма далолатнома | документы Didox (не сверяются) |
| 7 | Остаток (на счёте) | qoldiq | қолдиқ | `get_account_balances` |
| 8 | Входящий / исходящий остаток | kirish qoldig'i / chiqish qoldig'i | кириш қолдиғи / чиқиш қолдиғи | дневные остатки |
| 9 | Выписка (банковская) | bank ko'chirmasi | банк кўчирмаси | загрузка банка, импорт файлов |
| 10 | Поступление (приход) | tushum, kirim | тушум, кирим | `get_cash_flow` |
| 11 | Списание (расход) | chiqim, hisobdan chiqarish | чиқим | `get_cash_flow` |
| 12 | Платёж, оплата | to'lov | тўлов | банковские операции |
| 13 | Платёжное поручение | to'lov topshiriqnomasi | тўлов топшириқномаси | банковские операции |
| 14 | Назначение платежа | to'lov maqsadi | тўлов мақсади | `purpose` (сторонний текст) |
| 15 | Расчётный счёт | hisob raqami, hisob-kitob varag'i | ҳисоб рақами | банковские счета |
| 16 | МФО банка | bank MFO kodi | банк МФО коди | реквизиты |
| 17 | ИНН (налоговый номер) | STIR (soliq to'lovchining identifikatsiya raqami) | СТИР | `inn`, `tin` организации |
| 18 | ПИНФЛ (для физлица) | JShShIR | ЖШШИР | ИНН из 14 цифр |
| 19 | Контрагент | kontragent | контрагент | `find_counterparties` |
| 20 | Поставщик | yetkazib beruvchi | етказиб берувчи | исходящие платежи |
| 21 | Покупатель, клиент | xaridor, mijoz | харидор, мижоз | входящие платежи |
| 22 | Дебиторская задолженность | debitorlik qarzi | дебиторлик қарзи | `get_counterparty_balance_estimate` (оценка) |
| 23 | Кредиторская задолженность | kreditorlik qarzi | кредиторлик қарзи | `get_counterparty_balance_estimate` (оценка) |
| 24 | Долг, задолженность | qarz, qarzdorlik | қарз, қарздорлик | оценка по данным банка и Didox |
| 25 | Подпись (ЭЦП) | imzo (elektron raqamli imzo, ERI) | имзо (ЭРИ) | статусы документов |
| 26 | Подписан | imzolangan | имзоланган | статус 3 |
| 27 | Ожидает подписи | imzo kutilmoqda | имзо кутилмоқда | `get_documents_awaiting_our_signature` |
| 28 | Отклонён | rad etilgan | рад этилган | статус 4, `get_rejected_documents` |
| 29 | Аннулирован, недействителен | bekor qilingan | бекор қилинган | статус 40 |
| 30 | Черновик | qoralama | қоралама | статус 0 |
| 31 | НДС | QQS (qo'shilgan qiymat solig'i) | ҚҚС | `get_vat_summary` |
| 32 | Сумма с НДС / без НДС | QQS bilan / QQSsiz summa | ҚҚС билан / ҚҚСсиз сумма | суммы документов |
| 33 | Сверка (платежей с документами) | solishtirish, moslashtirish | солиштириш | `get_paid_documents` и др. |
| 34 | Сопоставление (возможное) | moslik (ehtimoliy) | мослик (эҳтимолий) | `reasons`, `confidence` |
| 35 | Неоплаченный документ | to'lanmagan hujjat | тўланмаган ҳужжат | `get_unpaid_documents` |
| 36 | Частичная оплата | qisman to'lov | қисман тўлов | `get_partially_paid_documents` |
| 37 | Двойная оплата | ikki marta to'lash | икки марта тўлаш | `get_possible_duplicate_payments` |
| 38 | Организация, предприятие | tashkilot, korxona | ташкилот, корхона | `organization` |
| 39 | Бухгалтерская компания (аутсорсинг) | buxgalteriya kompaniyasi (autsorsing) | бухгалтерия компанияси | аккаунт |
| 40 | Период, месяц, квартал | davr, oy, chorak | давр, ой, чорак | параметры `from`, `to` |
| 41 | Предупреждение, требует внимания | ogohlantirish, e'tibor talab qiladi | огоҳлантириш, эътибор талаб қилади | `get_attention_items` |
| 42 | Подключение (к банку, Didox) | ulanish | уланиш | `get_connections_overview` |

Сокращения правовых форм, которые нормализатор названий убирает при поиске: ООО / MChJ / МЧЖ (mas'uliyati cheklangan
jamiyat), ИП / ЯТТ / YaTT (yakka tartibdagi tadbirkor), ЧП (xususiy korxona, ХК), АО / АЖ / AJ (aksiyadorlik jamiyati).
