#ifndef UNITY_FIBONACCI_INCLUDED
#define UNITY_FIBONACCI_INCLUDED

// Computes a point using the Fibonacci sequence of length N.
// Input: Fib[N - 1], Fib[N - 2], and the index 'i' of the point.
// Ref: Integration of nonperiodic functions of two variables by Fibonacci lattice rules
float2 Fibonacci2dSeq(float fibN1, float fibN2, int i)
{
    // 3 cycles on GCN if 'fibN1' and 'fibN2' are known at compile time.
    return float2(i / fibN1, frac(i * (fibN2 / fibN1)));
}

static const int k_FibonacciSeq[] = {
    0, 1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377, 610, 987, 1597, 2584, 4181
};

static const float2 k_Fibonacci2dSeq21[] = {
    float2(0.00000000, 0.00000000),
    float2(0.04761905, 0.61904764),
    float2(0.09523810, 0.23809528),
    float2(0.14285715, 0.85714293),
    float2(0.19047619, 0.47619057),
    float2(0.23809524, 0.09523821),
    float2(0.28571430, 0.71428585),
    float2(0.33333334, 0.33333349),
    float2(0.38095239, 0.95238113),
    float2(0.42857143, 0.57142878),
    float2(0.47619048, 0.19047642),
    float2(0.52380955, 0.80952406),
    float2(0.57142860, 0.42857170),
    float2(0.61904764, 0.04761887),
    float2(0.66666669, 0.66666698),
    float2(0.71428573, 0.28571510),
    float2(0.76190478, 0.90476227),
    float2(0.80952382, 0.52380943),
    float2(0.85714287, 0.14285755),
    float2(0.90476191, 0.76190567),
    float2(0.95238096, 0.38095284)
};

static const float2 k_Fibonacci2dSeq34[] = {
    float2(0.00000000, 0.00000000),
    float2(0.02941176, 0.61764705),
    float2(0.05882353, 0.23529410),
    float2(0.08823530, 0.85294116),
    float2(0.11764706, 0.47058821),
    float2(0.14705883, 0.08823538),
    float2(0.17647059, 0.70588231),
    float2(0.20588236, 0.32352924),
    float2(0.23529412, 0.94117641),
    float2(0.26470590, 0.55882359),
    float2(0.29411766, 0.17647076),
    float2(0.32352942, 0.79411745),
    float2(0.35294119, 0.41176462),
    float2(0.38235295, 0.02941132),
    float2(0.41176471, 0.64705849),
    float2(0.44117647, 0.26470566),
    float2(0.47058824, 0.88235283),
    float2(0.50000000, 0.50000000),
    float2(0.52941179, 0.11764717),
    float2(0.55882353, 0.73529434),
    float2(0.58823532, 0.35294151),
    float2(0.61764705, 0.97058773),
    float2(0.64705884, 0.58823490),
    float2(0.67647058, 0.20588207),
    float2(0.70588237, 0.82352924),
    float2(0.73529410, 0.44117641),
    float2(0.76470590, 0.05882263),
    float2(0.79411763, 0.67646980),
    float2(0.82352942, 0.29411697),
    float2(0.85294116, 0.91176414),
    float2(0.88235295, 0.52941132),
    float2(0.91176468, 0.14705849),
    float2(0.94117647, 0.76470566),
    float2(0.97058821, 0.38235283)
};

static const float2 k_Fibonacci2dSeq55[] = {
    float2(0.00000000, 0.00000000),
    float2(0.01818182, 0.61818182),
    float2(0.03636364, 0.23636365),
    float2(0.05454545, 0.85454547),
    float2(0.07272727, 0.47272730),
    float2(0.09090909, 0.09090900),
    float2(0.10909091, 0.70909095),
    float2(0.12727273, 0.32727289),
    float2(0.14545454, 0.94545460),
    float2(0.16363636, 0.56363630),
    float2(0.18181819, 0.18181801),
    float2(0.20000000, 0.80000019),
    float2(0.21818182, 0.41818190),
    float2(0.23636363, 0.03636360),
    float2(0.25454545, 0.65454578),
    float2(0.27272728, 0.27272701),
    float2(0.29090908, 0.89090919),
    float2(0.30909091, 0.50909138),
    float2(0.32727271, 0.12727261),
    float2(0.34545454, 0.74545479),
    float2(0.36363637, 0.36363602),
    float2(0.38181818, 0.98181820),
    float2(0.40000001, 0.60000038),
    float2(0.41818181, 0.21818161),
    float2(0.43636364, 0.83636379),
    float2(0.45454547, 0.45454597),
    float2(0.47272727, 0.07272720),
    float2(0.49090910, 0.69090843),
    float2(0.50909090, 0.30909157),
    float2(0.52727270, 0.92727280),
    float2(0.54545456, 0.54545403),
    float2(0.56363636, 0.16363716),
    float2(0.58181816, 0.78181839),
    float2(0.60000002, 0.39999962),
    float2(0.61818182, 0.01818275),
    float2(0.63636363, 0.63636398),
    float2(0.65454543, 0.25454521),
    float2(0.67272729, 0.87272835),
    float2(0.69090909, 0.49090958),
    float2(0.70909089, 0.10909081),
    float2(0.72727275, 0.72727203),
    float2(0.74545455, 0.34545517),
    float2(0.76363635, 0.96363640),
    float2(0.78181821, 0.58181763),
    float2(0.80000001, 0.20000076),
    float2(0.81818181, 0.81818199),
    float2(0.83636361, 0.43636322),
    float2(0.85454547, 0.05454636),
    float2(0.87272727, 0.67272758),
    float2(0.89090908, 0.29090881),
    float2(0.90909094, 0.90909195),
    float2(0.92727274, 0.52727318),
    float2(0.94545454, 0.14545441),
    float2(0.96363634, 0.76363754),
    float2(0.98181820, 0.38181686)
};

static const float2 k_Fibonacci2dSeq89[] = {
    float2(0.00000000, 0.00000000),
    float2(0.01123596, 0.61797750),
    float2(0.02247191, 0.23595500),
    float2(0.03370786, 0.85393250),
    float2(0.04494382, 0.47191000),
    float2(0.05617978, 0.08988762),
    float2(0.06741573, 0.70786500),
    float2(0.07865169, 0.32584238),
    float2(0.08988764, 0.94382000),
    float2(0.10112359, 0.56179762),
    float2(0.11235955, 0.17977524),
    float2(0.12359551, 0.79775238),
    float2(0.13483146, 0.41573000),
    float2(0.14606741, 0.03370762),
    float2(0.15730338, 0.65168476),
    float2(0.16853933, 0.26966286),
    float2(0.17977528, 0.88764000),
    float2(0.19101124, 0.50561714),
    float2(0.20224719, 0.12359524),
    float2(0.21348314, 0.74157238),
    float2(0.22471911, 0.35955048),
    float2(0.23595506, 0.97752762),
    float2(0.24719101, 0.59550476),
    float2(0.25842696, 0.21348286),
    float2(0.26966292, 0.83146000),
    float2(0.28089887, 0.44943714),
    float2(0.29213482, 0.06741524),
    float2(0.30337077, 0.68539238),
    float2(0.31460676, 0.30336952),
    float2(0.32584271, 0.92134666),
    float2(0.33707866, 0.53932571),
    float2(0.34831461, 0.15730286),
    float2(0.35955057, 0.77528000),
    float2(0.37078652, 0.39325714),
    float2(0.38202247, 0.01123428),
    float2(0.39325842, 0.62921333),
    float2(0.40449437, 0.24719048),
    float2(0.41573033, 0.86516762),
    float2(0.42696628, 0.48314476),
    float2(0.43820226, 0.10112190),
    float2(0.44943821, 0.71910095),
    float2(0.46067417, 0.33707809),
    float2(0.47191012, 0.95505524),
    float2(0.48314607, 0.57303238),
    float2(0.49438202, 0.19100952),
    float2(0.50561798, 0.80898666),
    float2(0.51685393, 0.42696571),
    float2(0.52808988, 0.04494286),
    float2(0.53932583, 0.66292000),
    float2(0.55056179, 0.28089714),
    float2(0.56179774, 0.89887428),
    float2(0.57303369, 0.51685333),
    float2(0.58426964, 0.13483047),
    float2(0.59550560, 0.75280762),
    float2(0.60674155, 0.37078476),
    float2(0.61797750, 0.98876190),
    float2(0.62921351, 0.60673904),
    float2(0.64044946, 0.22471619),
    float2(0.65168542, 0.84269333),
    float2(0.66292137, 0.46067429),
    float2(0.67415732, 0.07865143),
    float2(0.68539327, 0.69662857),
    float2(0.69662923, 0.31460571),
    float2(0.70786518, 0.93258286),
    float2(0.71910113, 0.55056000),
    float2(0.73033708, 0.16853714),
    float2(0.74157304, 0.78651428),
    float2(0.75280899, 0.40449142),
    float2(0.76404494, 0.02246857),
    float2(0.77528089, 0.64044571),
    float2(0.78651685, 0.25842667),
    float2(0.79775280, 0.87640381),
    float2(0.80898875, 0.49438095),
    float2(0.82022470, 0.11235809),
    float2(0.83146065, 0.73033524),
    float2(0.84269661, 0.34831238),
    float2(0.85393256, 0.96628952),
    float2(0.86516851, 0.58426666),
    float2(0.87640452, 0.20224380),
    float2(0.88764048, 0.82022095),
    float2(0.89887643, 0.43820190),
    float2(0.91011238, 0.05617905),
    float2(0.92134833, 0.67415619),
    float2(0.93258429, 0.29213333),
    float2(0.94382024, 0.91011047),
    float2(0.95505619, 0.52808762),
    float2(0.96629214, 0.14606476),
    float2(0.97752810, 0.76404190),
    float2(0.98876405, 0.38201904)
};

// Loads elements from one of the precomputed tables for sample counts of 21, 34, 55.
// Computes sample positions at runtime otherwise.
// Sample count must be a Fibonacci number (see 'k_FibonacciSeq').
float2 Fibonacci2d(uint i, uint sampleCount)
{
    switch (sampleCount)
    {
        case 21: return k_Fibonacci2dSeq21[i];
        case 34: return k_Fibonacci2dSeq34[i];
        case 55: return k_Fibonacci2dSeq55[i];
        case 89: return k_Fibonacci2dSeq89[i];
        default:
        {
            int fibN1 = sampleCount;
            int fibN2 = sampleCount;

            // These are all constants, so this loop will be optimized away.
            for (int j = 1; j < 20; j++)
            {
                if (k_FibonacciSeq[j] == fibN1)
                {
                    fibN2 = k_FibonacciSeq[j - 1];
                }
            }

            return Fibonacci2dSeq(fibN1, fibN2, i);
        }
    }
}

#endif // UNITY_FIBONACCI_INCLUDED
