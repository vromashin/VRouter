namespace VR.RoutePlanner.Application.Robots;

/// <summary>
/// Кусок плана заказа: границы в узлах полного маршрута.
/// </summary>
/// <param name="StartIndex">Индекс первого узла куска в полном плане.</param>
/// <param name="NodeCount">Число узлов куска.</param>
public readonly record struct OrderChunk(int StartIndex, int NodeCount)
{
    /// <summary>Индекс последнего узла куска в полном плане.</summary>
    public int EndIndex => StartIndex + NodeCount - 1;
}

/// <summary>
/// Нарезка полного маршрута на куски для режима <c>Routing:Order:ReleasedNodes</c>.
///
/// Правило перенесено из AGVControl (<c>SplitPath</c>): при положительном значении маршрут
/// режется на куски по N узлов с перекрытием в один узел, при нулевом или отрицательном
/// весь маршрут уходит одним сообщением.
/// </summary>
public static class OrderChunkPlanner
{
    /// <summary>
    /// Делит маршрут на куски.
    /// </summary>
    /// <param name="nodeCount">Число узлов полного маршрута.</param>
    /// <param name="releasedNodes">Сколько узлов в куске; <c>0</c> и меньше — весь маршрут.</param>
    /// <returns>Куски в порядке проезда; пустой список для пустого маршрута.</returns>
    public static IReadOnlyList<OrderChunk> Slice(int nodeCount, int releasedNodes)
    {
        if (nodeCount <= 0)
        {
            return [];
        }

        if (releasedNodes <= 0 || releasedNodes >= nodeCount)
        {
            return [new OrderChunk(0, nodeCount)];
        }

        var chunks = new List<OrderChunk>();
        var step = releasedNodes - 1;
        for (var start = 0; start < nodeCount - 1; start += step)
        {
            chunks.Add(new OrderChunk(start, Math.Min(releasedNodes, nodeCount - start)));
            if (start + releasedNodes >= nodeCount)
            {
                break;
            }
        }

        return chunks;
    }
}
