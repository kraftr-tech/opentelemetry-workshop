// SPDX-FileCopyrightText: 2026 Cédric Moulard / Kraftr
// SPDX-License-Identifier: MIT

package main

import (
	"context"
	"fmt"
	"math/rand"
	"time"

	"github.com/open-feature/go-sdk/openfeature"
)


var declineReasons = []string{
	"insufficient_funds",
	"card_expired",
	"fraud_suspected",
	"issuer_unreachable",
}

type TransactionResult struct {
	ID            int32
	TransactionID string
	MerchantID    string
	Amount        float64
	Status        string
	CreatedAt     string
	DeclineReason string
}

func ProcessTransaction(ctx context.Context, merchantID string, amount float64) TransactionResult {
	// Latence simulee de base (workshop)
	time.Sleep(time.Duration(100+rand.Intn(400)) * time.Millisecond)

	// Latence supplementaire pilotee par feature flag
	extraMs, err := flagsClient.IntValue(ctx, "bankHighLatency", 0, openfeature.EvaluationContext{})
	if err == nil && extraMs > 0 {
		time.Sleep(time.Duration(extraMs) * time.Millisecond)
	}

	transactionID := fmt.Sprintf("TXN-%d", time.Now().UnixNano())
	status := "approved"
	declineReason := ""

	// Taux de rejet : flag bankDeclineRate prend le pas, sinon baseline ~10%
	declineRate, err := flagsClient.FloatValue(ctx, "bankDeclineRate", 0.0, openfeature.EvaluationContext{})
	if err != nil {
		declineRate = 0.0
	}
	effectiveDeclineRate := 0.1
	if declineRate > 0 {
		effectiveDeclineRate = declineRate
	}

	if rand.Float64() < effectiveDeclineRate {
		status = "declined"
		declineReason = declineReasons[rand.Intn(len(declineReasons))]
	}

	return TransactionResult{
		ID:            int32(rand.Intn(100000)),
		TransactionID: transactionID,
		MerchantID:    merchantID,
		Amount:        amount,
		Status:        status,
		CreatedAt:     time.Now().UTC().Format(time.RFC3339),
		DeclineReason: declineReason,
	}
}
