# Реверс: движение симулятора по ребру

1. **Вопрос**
   Как VDA-симулятор получает trajectory заказа, ведет робота по ребру и при каких условиях движение/state останавливаются.

2. **Точки входа**
   - Модель VDA-погрузчика вызывает `logic()->integrate()` и затем `VDARobotModel::integrate(dt)`: `C:\Temp\robotssimulator\assets\VDARobots\VDAForkLiftPlugin\src\RobotsModel\VDAForkLiftModel.cpp:500-536`.
   - Цикл физической модели: `RobotSimulatorBase::run()`, период сна 30 мс: `C:\Temp\robotssimulator\core\src\RobotsModel\RobotSimulatorBase.cpp:110-152`.
   - Важно: `core/vdalogic` является соседним репозиторием и отсутствует в дереве simulator: `C:\Temp\robotssimulator\docs\vdalogic.md:3,9`; реализация MotionController/RobotClient здесь не проверена.

3. **Поток**
   - Модель доходит до `motionController()->integrate()`, затем `doControl()`, только в AUTOMATIC/SEMIAUTOMATIC: `C:\Temp\robotssimulator\assets\VDARobots\VDAForkLiftPlugin\src\RobotsModel\VDARobotModel.cpp:365-372`.
   - На ребре контроллер запрашивает `trajFollow`; при false немедленно выходит из `doControl`: `...\VDARobotModel.cpp:140-145`.
   - При успехе использует LUT, ищет ближайший семпл и точку впереди по длине: `...\VDARobotModel.cpp:147-217`; направление задает lookahead point: `...\VDARobotModel.cpp:219-228`.
   - Непосредственно интегратор VDA не содержит разбора JSON trajectory: parser и вызов `trajFollow` находятся в отсутствующем `vdalogic` (см. `C:\Temp\robotssimulator\docs\vdalogic.md:9,154-165`).

4. **Поля и значения**
   | Поле | Значение/тип | Где задано | Зачем |
   |---|---|---|---|
   | trajectory, degree, knotVector, controlPoints, weight | Не проверено: исходник парсера не в `C:\Temp\robotssimulator`; `docs/vdalogic.md:9,31` только называет `TrajectoryFollower` NURBS. | отсутствующий `core/vdalogic` | Определить фактический тип/сохранение невозможно. |
   | LUT | `QVector<TrajectorySample>` | `...\VDARobotModel.cpp:147` | Семплы траектории, потребляемые контроллером. |
   | lookaheadDistance | `0.5f` | `...\VDARobotModel.cpp:198-209` | Выбор цели впереди вдоль LUT. |
   | max speed | `1.0f` | `...\VDARobotModel.cpp:22` | Ограничение линейной скорости контроллера. |

5. **Отличия и дефекты**
   - Документация описывает NURBS только для ребра с не менее 3 контрольными точками; иначе прямой отрезок, LUT из 151 выборки: `C:\Temp\robotssimulator\docs\vdalogic.md:161`. Это документация соседней логики, код и валидация не проверены.
   - Случаи degree=3 с недостаточными точками и несогласованного knotVector: не проверено, конкретное поведение (исключение/лог/отказ) по доступному исходнику не найдено.
   - Сам VDA-плагин использует LUT, а не вычисляет NURBS-точку напрямую: `...\VDARobotModel.cpp:147-217`. Формула NURBS/Безье по t/u не найдена в симуляторе.
   - Заявленная в вопросе строка `Stop on Node "<node>-<seq>" for rotate or actions` и такой лог не найдены в исходниках `C:\Temp\robotssimulator` (искал `Stop on Node`, `start:`, `end:`, `trajectory`). Причина конкретного зависания не доказана.
   - Прямой тест LUT полагает, что расстояние по кривой сначала убывает, затем возрастает и прекращает поиск на первом росте: `...\VDARobotModel.cpp:176-192`; для немонотонной/самопересекающейся кривой поиск может выбрать не глобально ближайший семпл.

6. **Что повторить у нас**
   - Следовать форме trajectory заказа: в документации симулятора описано применение NURBS при >=3 точках, иначе отрезок (`C:\Temp\robotssimulator\docs\vdalogic.md:161`), но подтвердить это кодом `vdalogic` до реализации.
   - Отдельно проверить LUT generation, bounds/валидность degree, knotVector и число контрольных точек: соответствующие исходники не доступны, никаких правил валидации не переносить как установленный факт.
   - Учесть ранний выход контроллера при `trajFollow()==false`: `...\VDARobotModel.cpp:144-145`; отсутствие LUT может оставить заданное управление нулевым на шаге (`...\VDARobotModel.cpp:104`).

7. **Что проверить на стенде**
   - Сравнить два заказа на `wa148 -> wa104`: 4 контрольные точки/degree 3 и прямая/degree 1; записать полный VDA state и статус active node/edge после каждого шага.
   - Для каждого повторить с корректной и заведомо некорректной парой degree/controlPoints/knotVector; собрать лог исключений и факт продолжения публикации state. Конкретную реакцию исходник simulator не подтверждает.
   - Проверить повтор order update с теми же узлами и новыми sequenceId. Документация `vdalogic` требует совпадения первого узла продолжения по id и sequenceId: `C:\Temp\robotssimulator\docs\vdalogic.md:112`; реализация не проверена.
   - Проверить pause, отсутствие isDriving, отсутствие LUT и obstacle braking отдельно; модель выходит на pause/not-driving: `...\VDARobotModel.cpp:365-366`, на blocked route: `...\VDARobotModel.cpp:374-378`.
   - State управляется отдельным MQTT-клиентом: по документации таймеру и событиям (`C:\Temp\robotssimulator\docs\vdalogic.md:92-103,122`); реализация и условие прекращения публикации в исходниках simulator не проверены. При выключенном питании интеграция не вызывается: `C:\Temp\robotssimulator\core\src\RobotsModel\RobotSimulatorBase.cpp:115-129`.
   - Логика остановки на узле/action/rotate и снятия этого состояния: не проверено без `vdalogic`; по docs текущая action завершает блокировку только на FINISHED/FAILED (`C:\Temp\robotssimulator\docs\vdalogic.md:142-149`). Условия вечного ожидания в реализации не установлены.
   - Физика поворота: модель задает угловую скорость по ошибке курса (`...\VDARobotModel.cpp:222-250`), linear speed равна нулю при ошибке >=5 градусов (`...\VDARobotModel.cpp:252-260`). Предиктивное торможение сканирует препятствия на минимальной скорости и ставит blocked при столкновении: `...\VDARobotModel.cpp:634-656`; при blocked дальнейшая динамика не интегрируется (`...\VDARobotModel.cpp:374-378`).
   - Геометрическая collision-проверка есть в `CollisionService::tick`: `C:\Temp\robotssimulator\src\WorldServer\CollisionService.cpp:180-207`, но вызов этого тика закомментирован в world loop: `C:\Temp\robotssimulator\src\WorldServer\WorldServer.cpp:268-272`. «Робот уже в препятствии» через этот сервис не проверяется на каждом тике; inflation map для маршрута не найдено.
