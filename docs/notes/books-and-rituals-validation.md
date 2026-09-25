# Проверка книг, умений и ритуалов

Результаты на 25 сентября 2026 года, .NET SDK 9.0.318.

## Результаты

- Content.Client и Content.Server собираются без ошибок.
- Успешно проверены все 25 целевых сценариев из интеграционного проекта.
  Первоначальный прогон дал 22 успешных проверки; после исправлений связанные
  RitualSystemTest и RitualVesselTest повторно прошли целиком — 6 из 6.
- Отдельный RitualLiquidTest в Content.Tests прошёл — 1 из 1.
- Все восемь новых файлов YAML разбираются YamlDotNet; ссылки каталога на
  53 знания разрешаются, определены 24 ритуала. Проверены используемые RSI и состояния.
- Проверка конфликтующих регистраций событий и `git diff --check` пройдена.

Проверены однократное обучение и перевод, цена оригиналов, перенос знаний,
языков и действий между телами, шесть сценариев обычных умений, выбор даров
Магнуса, возврат настоящего предмета, завершение проницаемости, антимагия,
резервирование и отмена подношений, сохранение сосуда при расходе воды,
накопление урона и усталости под Валтором, восстановление голоса личины,
пять случаев обмена областей, сохранение экипировки оживлённого доспеха
и перенос реального раствора между связанными сосудами.

## Повторение

На момент проверки общий тестовый проект не компилировался из-за существующего
черновика `Tests/Imperial/Medieval/Skills/SkillProgressionTest.cs`: он ссылается
на отсутствующее пространство имён `Content.Server.Imperial.Medieval.Skills.Progression`.
Файлы навыков не изменялись. Для целевой сборки применялся временный файл
MSBuild вне репозитория, передаваемый через `-p:CustomAfterMicrosoftCommonTargets=...`:

```xml
<Project>
  <Target Name="ExcludeUnfinishedSkillTestsForBookValidation" BeforeTargets="CoreCompile">
    <ItemGroup Condition="'$(MSBuildProjectName)' == 'Content.IntegrationTests'">
      <Compile Remove="Tests/Imperial/Medieval/Skills/**/*.cs" />
    </ItemGroup>
  </Target>
</Project>
```

После сборки тестового проекта целевой набор запускается так:

```powershell
dotnet test Content.IntegrationTests/Content.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~BookLearningTest|FullyQualifiedName~BookAbilityTest|FullyQualifiedName~RitualBoundaryTest|FullyQualifiedName~RitualSystemTest|FullyQualifiedName~RitualTheftPlannerTest|FullyQualifiedName~MagnusGiftTest|FullyQualifiedName~RitualVesselTest" -- NUnit.NumberOfTestWorkers=1
dotnet test Content.Tests/Content.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~RitualLiquidTest"
```

## Что не проверено этим набором

Ручной раунд с игроками и полный балансный прогон не проводились. Закрытого
синтезатора TTS здесь нет: для пространственного чревовещания его обработчику
нужно использовать `EntitySpokeEvent.SoundSource` как позицию звука и сохранить
`Source` как настоящего автора. Перенос обычного звука, чата и речевого пузыря
реализован; синтезированный голос требует проверки в сборке с этим модулем.
