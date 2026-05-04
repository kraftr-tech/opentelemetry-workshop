// SPDX-FileCopyrightText: 2026 Cedric Moulard / Kraftr
// SPDX-License-Identifier: MIT

package main

import (
	"os"
	"strconv"

	flagd "github.com/open-feature/go-sdk-contrib/providers/flagd/pkg"
	"github.com/open-feature/go-sdk/openfeature"
)

var flagsClient *openfeature.Client

func initFlags() {
	host := os.Getenv("FLAGD_HOST")
	if host == "" {
		host = "flagd"
	}
	port, err := strconv.Atoi(os.Getenv("FLAGD_PORT"))
	if err != nil || port == 0 {
		port = 8013
	}

	provider, err := flagd.NewProvider(
		flagd.WithHost(host),
		flagd.WithPort(uint16(port)),
	)
	if err != nil {
		panic("failed to create flagd provider: " + err.Error())
	}

	if err := openfeature.SetProvider(provider); err != nil {
		panic("failed to set flagd provider: " + err.Error())
	}

	flagsClient = openfeature.NewClient("bank-service")
}
